"""Structured dual-faction policy architecture for Bees RL.

The Unity policy ABI remains one fixed vector/action contract and one deployable
BeesRL1v1 model. Inside that model, Bees replaces ML-Agents' flat vector MLP with:

* shared entity encoders and masked attention pooling;
* a shared semantic weapon encoder reused by observed and self weapons;
* per-slot weapon embeddings retained for shared weapon action heads;
* independent Bee and Human actor encoders, trunks, and action heads selected by an
  explicit faction observation channel; and
* the ordinary ML-Agents MA-POCA centralized critic, which automatically consumes
  the structured observation encoding through the patched ObservationEncoder.

The patch is deliberately limited to the exact Bees v22 observation/action shapes.
All other ML-Agents behaviors retain the upstream implementation.
"""

from __future__ import annotations

import contextvars
import math
from typing import Mapping, Optional

from mlagents.torch_utils import torch, nn
from mlagents.trainers.exception import UnityTrainerException
from mlagents.trainers.torch_entities.action_model import (
    ActionModel as _OriginalActionModel,
    DistInstances,
)
from mlagents.trainers.torch_entities.distributions import (
    CategoricalDistInstance,
    GaussianDistInstance,
    GaussianDistribution,
)
from mlagents.trainers.torch_entities.encoders import VectorInput
from mlagents.trainers.torch_entities.layers import (
    Initialization,
    LinearEncoder,
    linear_layer,
)
from mlagents.trainers.torch_entities.networks import (
    NetworkBody as _OriginalNetworkBody,
    ObservationEncoder as _OriginalObservationEncoder,
)
from mlagents.trainers.trajectory import ObsUtil


BEES_OBSERVATION_SIZE = 7743
BEES_CONTINUOUS_ACTIONS = 16
BEES_DISCRETE_BRANCHES = (2, 2, 2, 2, 2, 5)
BEES_WEAPON_SLOTS = 5

SELF_START = 0
SELF_SIZE = 25
CAPABILITY_START = 25
CAPABILITY_SIZE = 12
PARENT_START = 37
PARENT_SIZE = 41
ALLY_START = 78
ALLY_COUNT = 64
ALLY_SIZE = 45
ENEMY_START = ALLY_START + ALLY_COUNT * ALLY_SIZE
ENEMY_COUNT = 64
ENEMY_SIZE = 41
SELF_WEAPON_START = ENEMY_START + ENEMY_COUNT * ENEMY_SIZE
SELF_WEAPON_SIZE = 15
MINING_START = SELF_WEAPON_START + BEES_WEAPON_SLOTS * SELF_WEAPON_SIZE
MINING_COUNT = 8
MINING_SIZE = 7
MAP_OBJECT_START = MINING_START + MINING_COUNT * MINING_SIZE
MAP_OBJECT_COUNT = 64
MAP_OBJECT_SIZE = 12
COLLISION_START = MAP_OBJECT_START + MAP_OBJECT_COUNT * MAP_OBJECT_SIZE
COLLISION_COUNT = 48
COLLISION_SIZE = 11
OBJECTIVE_START = COLLISION_START + COLLISION_COUNT * COLLISION_SIZE
OBJECTIVE_SIZE = 16
NAVIGATION_START = OBJECTIVE_START + OBJECTIVE_SIZE
NAVIGATION_SIZE = 441
EXPLORATION_START = NAVIGATION_START + NAVIGATION_SIZE
EXPLORATION_SIZE = 256
EPISODE_PROGRESS_INDEX = EXPLORATION_START + EXPLORATION_SIZE
FACTION_INDEX = EPISODE_PROGRESS_INDEX + 1
RESERVED_START = FACTION_INDEX + 1
RESERVED_SIZE = BEES_OBSERVATION_SIZE - RESERVED_START

ENTITY_BASE_SIZE = 16
OBSERVED_WEAPON_SIZE = 5
ENTITY_WEAPON_COUNT = 5

WEAPON_COMMON_EMBED = 32
WEAPON_SLOT_EMBED = 64
ENTITY_EMBED = 96
SELF_EMBED = 96
CONTEXT_SIZE = 512
FACTION_TRUNK_SIZE = 128
ACTION_ENCODING_SIZE = (
    FACTION_TRUNK_SIZE + BEES_WEAPON_SLOTS * WEAPON_SLOT_EMBED + 1
)

_STRUCTURED_ENCODER_MARKER = "bees_structured_v23"
_INSTALLED_STATE = None
_TRAINING_SLOT_LIMITS = contextvars.ContextVar(
    "bees_structured_training_slot_limits",
    default=None,
)


def set_training_slot_limits(limits: Optional[Mapping[str, int]]):
    return _TRAINING_SLOT_LIMITS.set(None if limits is None else dict(limits))


def reset_training_slot_limits(token) -> None:
    _TRAINING_SLOT_LIMITS.reset(token)


def _slot_limit(name: str, full_count: int) -> int:
    limits = _TRAINING_SLOT_LIMITS.get()
    if not limits or torch.onnx.is_in_onnx_export():
        return full_count
    value = limits.get(name)
    if not isinstance(value, int):
        return full_count
    return max(1, min(full_count, value))


def _use_dense_structured_path() -> bool:
    """Use fixed-shape CUDA work for gradient updates and ONNX export.

    The sparse rollout path skips empty padded slots with torch.nonzero(), which is useful for
    small no-grad inference batches. On CUDA, nonzero() synchronizes the host with the device;
    POCA invokes the structured encoder many times per optimizer minibatch, so those synchronizing
    sparse selections can dominate gradient-update time. Training therefore uses the equivalent
    dense masked computation while no-grad rollout inference keeps the sparse path.
    """

    return bool(
        torch.is_grad_enabled()
        or torch.onnx.is_in_onnx_export()
        or _TRAINING_SLOT_LIMITS.get()
    )


def _is_bees_observation_specs(observation_specs) -> bool:
    return (
        len(observation_specs) == 1
        and tuple(observation_specs[0].shape) == (BEES_OBSERVATION_SIZE,)
    )


def _is_bees_action_spec(action_spec) -> bool:
    return (
        action_spec is not None
        and int(action_spec.continuous_size) == BEES_CONTINUOUS_ACTIONS
        and tuple(int(value) for value in action_spec.discrete_branches)
        == BEES_DISCRETE_BRANCHES
    )


def _mlp(input_size: int, output_size: int) -> nn.Sequential:
    return nn.Sequential(
        linear_layer(
            input_size,
            output_size,
            kernel_init=Initialization.KaimingHeNormal,
            kernel_gain=1.41,
        ),
        nn.LeakyReLU(),
        linear_layer(
            output_size,
            output_size,
            kernel_init=Initialization.KaimingHeNormal,
            kernel_gain=1.41,
        ),
        nn.LeakyReLU(),
    )


class _MaskedAttentionPool(nn.Module):
    def __init__(self, query_size: int, entity_size: int, attention_size: int) -> None:
        super().__init__()
        self.query = linear_layer(
            query_size,
            attention_size,
            kernel_init=Initialization.KaimingHeNormal,
            kernel_gain=1.0,
        )
        self.key = linear_layer(
            entity_size,
            attention_size,
            kernel_init=Initialization.KaimingHeNormal,
            kernel_gain=1.0,
        )
        self.scale = math.sqrt(float(attention_size))

    def forward(
        self,
        query: torch.Tensor,
        entities: torch.Tensor,
        presence: torch.Tensor,
    ) -> torch.Tensor:
        q = self.query(query).unsqueeze(1)
        k = self.key(entities)
        scores = torch.sum(q * k, dim=2) / self.scale
        mask = torch.clamp(presence, 0.0, 1.0)
        masked_scores = scores + (1.0 - mask) * -10000.0
        weights = torch.softmax(masked_scores, dim=1) * mask
        weights = weights / torch.clamp(
            torch.sum(weights, dim=1, keepdim=True),
            min=1.0e-6,
        )
        return torch.sum(entities * weights.unsqueeze(2), dim=1)


class _MaskedScalarPool(nn.Module):
    def __init__(self, entity_size: int) -> None:
        super().__init__()
        self.score = linear_layer(
            entity_size,
            1,
            kernel_init=Initialization.KaimingHeNormal,
            kernel_gain=1.0,
        )

    def forward(
        self,
        entities: torch.Tensor,
        presence: torch.Tensor,
    ) -> torch.Tensor:
        scores = self.score(entities).squeeze(2)
        mask = torch.clamp(presence, 0.0, 1.0)
        masked_scores = scores + (1.0 - mask) * -10000.0
        weights = torch.softmax(masked_scores, dim=1) * mask
        weights = weights / torch.clamp(
            torch.sum(weights, dim=1, keepdim=True),
            min=1.0e-6,
        )
        return torch.sum(entities * weights.unsqueeze(2), dim=1)


class BeesStructuredObservationEncoder(nn.Module):
    """Reshape the frozen Bees vector into shared semantic entity encoders."""

    def __init__(
        self,
        observation_specs,
        h_size: int,
        vis_encode_type,
        normalize: bool = False,
    ) -> None:
        super().__init__()
        self._bees = _is_bees_observation_specs(observation_specs)
        if not self._bees:
            self._fallback = _OriginalObservationEncoder(
                observation_specs,
                h_size,
                vis_encode_type,
                normalize,
            )
            self._total_enc_size = self._fallback.total_enc_size
            self._total_goal_enc_size = self._fallback.total_goal_enc_size
            return

        self._fallback = None
        self.normalize = normalize
        self.vector_input = VectorInput(BEES_OBSERVATION_SIZE, normalize=normalize)

        self.weapon_common_encoder = _mlp(
            OBSERVED_WEAPON_SIZE,
            WEAPON_COMMON_EMBED,
        )
        self.entity_weapon_pool = _MaskedScalarPool(WEAPON_COMMON_EMBED)
        self.entity_base_encoder = _mlp(ENTITY_BASE_SIZE, 64)
        self.entity_fuse = _mlp(64 + WEAPON_COMMON_EMBED, ENTITY_EMBED)

        self.self_encoder = _mlp(SELF_SIZE, SELF_EMBED)
        self.capability_encoder = _mlp(CAPABILITY_SIZE, 32)
        self.ally_communication_encoder = _mlp(4, 16)
        self.ally_fuse = _mlp(ENTITY_EMBED + 16, ENTITY_EMBED)

        self.self_weapon_specific_encoder = _mlp(10, 32)
        self.self_weapon_fuse = _mlp(
            WEAPON_COMMON_EMBED + 32,
            WEAPON_SLOT_EMBED,
        )

        self.ally_pool = _MaskedAttentionPool(SELF_EMBED, ENTITY_EMBED, 64)
        self.enemy_pool = _MaskedAttentionPool(SELF_EMBED, ENTITY_EMBED, 64)
        self.self_weapon_pool = _MaskedAttentionPool(
            SELF_EMBED,
            WEAPON_SLOT_EMBED,
            48,
        )

        self.mining_encoder = _mlp(MINING_SIZE, 48)
        self.map_object_encoder = _mlp(MAP_OBJECT_SIZE, 64)
        self.collision_encoder = _mlp(COLLISION_SIZE, 64)
        self.mining_pool = _MaskedAttentionPool(SELF_EMBED, 48, 32)
        self.map_object_pool = _MaskedAttentionPool(SELF_EMBED, 64, 48)
        self.collision_pool = _MaskedAttentionPool(SELF_EMBED, 64, 48)

        self.objective_encoder = _mlp(OBJECTIVE_SIZE, 32)
        self.navigation_encoder = _mlp(NAVIGATION_SIZE, 96)
        self.exploration_encoder = _mlp(EXPLORATION_SIZE, 64)
        self.tail_encoder = _mlp(1 + RESERVED_SIZE, 32)

        context_input_size = (
            SELF_EMBED
            + 32
            + ENTITY_EMBED
            + ENTITY_EMBED
            + ENTITY_EMBED
            + WEAPON_SLOT_EMBED
            + 48
            + 64
            + 64
            + 32
            + 96
            + 64
            + 32
        )
        self.context_fuse = _mlp(context_input_size, CONTEXT_SIZE)

        self._total_enc_size = (
            CONTEXT_SIZE + BEES_WEAPON_SLOTS * WEAPON_SLOT_EMBED + 1
        )
        self._total_goal_enc_size = 0
        self._processors = [self.vector_input]

    @property
    def processors(self):
        if self._fallback is not None:
            return self._fallback.processors
        return self._processors

    @property
    def embedding_sizes(self):
        if self._fallback is not None:
            return self._fallback.embedding_sizes
        return [self._total_enc_size]

    @property
    def total_enc_size(self) -> int:
        return self._total_enc_size

    @property
    def total_goal_enc_size(self) -> int:
        return self._total_goal_enc_size

    def update_normalization(self, buffer) -> None:
        if self._fallback is not None:
            self._fallback.update_normalization(buffer)
            return
        obs = ObsUtil.from_buffer(buffer, 1)
        values = torch.as_tensor(obs[0].to_ndarray())
        normalizer = self.vector_input.normalizer
        if normalizer is not None:
            values = values.to(
                device=normalizer.running_mean.device,
                dtype=normalizer.running_mean.dtype,
            )
        self.vector_input.update_normalization(values)

    def copy_normalization(self, other_encoder) -> None:
        if self._fallback is not None:
            target = (
                other_encoder._fallback
                if isinstance(other_encoder, BeesStructuredObservationEncoder)
                else other_encoder
            )
            self._fallback.copy_normalization(target)
            return
        if (
            isinstance(other_encoder, BeesStructuredObservationEncoder)
            and other_encoder._fallback is None
        ):
            self.vector_input.copy_normalization(other_encoder.vector_input)

    def get_goal_encoding(self, inputs):
        if self._fallback is not None:
            return self._fallback.get_goal_encoding(inputs)
        raise UnityTrainerException(
            "The Bees combat policy does not define goal-signal observations."
        )

    def _entity_weapon_embeddings(
        self,
        normalized: torch.Tensor,
        raw: torch.Tensor,
    ) -> torch.Tensor:
        batch = normalized.shape[0]
        entity_count = normalized.shape[1]
        weapons = normalized[:, :, ENTITY_BASE_SIZE:].reshape(
            batch,
            entity_count,
            ENTITY_WEAPON_COUNT,
            OBSERVED_WEAPON_SIZE,
        )
        raw_weapons = raw[:, :, ENTITY_BASE_SIZE:].reshape(
            batch,
            entity_count,
            ENTITY_WEAPON_COUNT,
            OBSERVED_WEAPON_SIZE,
        )
        if _use_dense_structured_path():
            weapon_count = _slot_limit(
                "entity_weapons",
                ENTITY_WEAPON_COUNT,
            )
            weapons = weapons[:, :, :weapon_count, :]
            raw_weapons = raw_weapons[:, :, :weapon_count, :]
            flat = weapons.reshape(-1, OBSERVED_WEAPON_SIZE)
            embedded = self.weapon_common_encoder(flat).reshape(
                batch,
                entity_count,
                weapon_count,
                WEAPON_COMMON_EMBED,
            )
            presence = raw_weapons[:, :, :, 0]
            embedded = embedded * torch.clamp(
                presence,
                0.0,
                1.0,
            ).unsqueeze(3)
            pooled = self.entity_weapon_pool(
                embedded.reshape(
                    batch * entity_count,
                    weapon_count,
                    WEAPON_COMMON_EMBED,
                ),
                presence.reshape(batch * entity_count, weapon_count),
            )
            return pooled.reshape(
                batch,
                entity_count,
                WEAPON_COMMON_EMBED,
            )

        flat = weapons.reshape(-1, OBSERVED_WEAPON_SIZE)
        flat_presence = torch.clamp(
            raw_weapons[:, :, :, 0].reshape(-1),
            0.0,
            1.0,
        )
        embedded_flat = flat.new_zeros((flat.shape[0], WEAPON_COMMON_EMBED))
        active = torch.nonzero(flat_presence > 0.0, as_tuple=False).squeeze(1)
        if active.numel() > 0:
            active_embedding = self.weapon_common_encoder(
                flat.index_select(0, active)
            )
            active_embedding = active_embedding * flat_presence.index_select(
                0,
                active,
            ).unsqueeze(1)
            embedded_flat = embedded_flat.index_copy(
                0,
                active,
                active_embedding,
            )
        embedded = embedded_flat.reshape(
            batch,
            entity_count,
            ENTITY_WEAPON_COUNT,
            WEAPON_COMMON_EMBED,
        )
        presence = flat_presence.reshape(
            batch,
            entity_count,
            ENTITY_WEAPON_COUNT,
        )
        pooled = self.entity_weapon_pool(
            embedded.reshape(
                batch * entity_count,
                ENTITY_WEAPON_COUNT,
                WEAPON_COMMON_EMBED,
            ),
            presence.reshape(batch * entity_count, ENTITY_WEAPON_COUNT),
        )
        return pooled.reshape(
            batch,
            entity_count,
            WEAPON_COMMON_EMBED,
        )

    def _encode_entities(
        self,
        normalized: torch.Tensor,
        raw: torch.Tensor,
    ) -> torch.Tensor:
        if _use_dense_structured_path():
            base = self.entity_base_encoder(
                normalized[:, :, :ENTITY_BASE_SIZE]
            )
            weapons = self._entity_weapon_embeddings(normalized, raw)
            embedded = self.entity_fuse(torch.cat([base, weapons], dim=2))
            return embedded * torch.clamp(
                raw[:, :, 0],
                0.0,
                1.0,
            ).unsqueeze(2)

        batch = normalized.shape[0]
        entity_count = normalized.shape[1]
        presence = torch.clamp(raw[:, :, 0], 0.0, 1.0)
        flat_presence = presence.reshape(-1)
        active = torch.nonzero(flat_presence > 0.0, as_tuple=False).squeeze(1)
        output = normalized.new_zeros((batch * entity_count, ENTITY_EMBED))
        if active.numel() == 0:
            return output.reshape(batch, entity_count, ENTITY_EMBED)

        flat_normalized = normalized.reshape(-1, normalized.shape[2])
        flat_raw = raw.reshape(-1, raw.shape[2])
        active_normalized = flat_normalized.index_select(0, active)
        active_raw = flat_raw.index_select(0, active)
        base = self.entity_base_encoder(
            active_normalized[:, :ENTITY_BASE_SIZE]
        )
        weapons = self._entity_weapon_embeddings(
            active_normalized.unsqueeze(1),
            active_raw.unsqueeze(1),
        )[:, 0, :]
        embedded = self.entity_fuse(torch.cat([base, weapons], dim=1))
        embedded = embedded * flat_presence.index_select(
            0,
            active,
        ).unsqueeze(1)
        output = output.index_copy(0, active, embedded)
        return output.reshape(batch, entity_count, ENTITY_EMBED)

    def _encode_allies(
        self,
        normalized: torch.Tensor,
        raw: torch.Tensor,
    ):
        if _use_dense_structured_path():
            entity = self._encode_entities(
                normalized[:, :, :ENEMY_SIZE],
                raw[:, :, :ENEMY_SIZE],
            )
            communication = self.ally_communication_encoder(
                normalized[:, :, ENEMY_SIZE:ALLY_SIZE]
            )
            embedded = self.ally_fuse(
                torch.cat([entity, communication], dim=2)
            )
            presence = torch.clamp(raw[:, :, 0], 0.0, 1.0)
            return embedded * presence.unsqueeze(2), presence

        batch = normalized.shape[0]
        ally_count = normalized.shape[1]
        presence = torch.clamp(raw[:, :, 0], 0.0, 1.0)
        flat_presence = presence.reshape(-1)
        active = torch.nonzero(flat_presence > 0.0, as_tuple=False).squeeze(1)
        output = normalized.new_zeros((batch * ally_count, ENTITY_EMBED))
        if active.numel() == 0:
            return output.reshape(batch, ally_count, ENTITY_EMBED), presence

        flat_normalized = normalized.reshape(-1, normalized.shape[2])
        flat_raw = raw.reshape(-1, raw.shape[2])
        active_normalized = flat_normalized.index_select(0, active)
        active_raw = flat_raw.index_select(0, active)
        entity = self._encode_entities(
            active_normalized[:, :ENEMY_SIZE].unsqueeze(1),
            active_raw[:, :ENEMY_SIZE].unsqueeze(1),
        )[:, 0, :]
        communication = self.ally_communication_encoder(
            active_normalized[:, ENEMY_SIZE:ALLY_SIZE]
        )
        embedded = self.ally_fuse(
            torch.cat([entity, communication], dim=1)
        )
        embedded = embedded * flat_presence.index_select(
            0,
            active,
        ).unsqueeze(1)
        output = output.index_copy(0, active, embedded)
        return output.reshape(batch, ally_count, ENTITY_EMBED), presence

    def _encode_self_weapons(
        self,
        normalized: torch.Tensor,
        raw: torch.Tensor,
    ) -> torch.Tensor:
        if _use_dense_structured_path():
            common = torch.stack(
                [
                    normalized[:, :, 0],
                    normalized[:, :, 1],
                    normalized[:, :, 4],
                    normalized[:, :, 5],
                    normalized[:, :, 6],
                ],
                dim=2,
            )
            specific = torch.stack(
                [
                    normalized[:, :, 2],
                    normalized[:, :, 3],
                    normalized[:, :, 7],
                    normalized[:, :, 8],
                    normalized[:, :, 9],
                    normalized[:, :, 10],
                    normalized[:, :, 11],
                    normalized[:, :, 12],
                    normalized[:, :, 13],
                    normalized[:, :, 14],
                ],
                dim=2,
            )
            common_embedding = self.weapon_common_encoder(common)
            specific_embedding = self.self_weapon_specific_encoder(specific)
            embedded = self.self_weapon_fuse(
                torch.cat([common_embedding, specific_embedding], dim=2)
            )
            return embedded * torch.clamp(
                raw[:, :, 0],
                0.0,
                1.0,
            ).unsqueeze(2)

        batch = normalized.shape[0]
        weapon_count = normalized.shape[1]
        presence = torch.clamp(raw[:, :, 0], 0.0, 1.0)
        flat_presence = presence.reshape(-1)
        active = torch.nonzero(flat_presence > 0.0, as_tuple=False).squeeze(1)
        output = normalized.new_zeros((batch * weapon_count, WEAPON_SLOT_EMBED))
        if active.numel() == 0:
            return output.reshape(batch, weapon_count, WEAPON_SLOT_EMBED)

        flat = normalized.reshape(-1, SELF_WEAPON_SIZE)
        active_values = flat.index_select(0, active)
        common = active_values[:, [0, 1, 4, 5, 6]]
        specific = active_values[:, [2, 3, 7, 8, 9, 10, 11, 12, 13, 14]]
        common_embedding = self.weapon_common_encoder(common)
        specific_embedding = self.self_weapon_specific_encoder(specific)
        embedded = self.self_weapon_fuse(
            torch.cat([common_embedding, specific_embedding], dim=1)
        )
        embedded = embedded * flat_presence.index_select(
            0,
            active,
        ).unsqueeze(1)
        output = output.index_copy(0, active, embedded)
        return output.reshape(batch, weapon_count, WEAPON_SLOT_EMBED)

    def _encode_set(self, normalized, raw, encoder, output_size: int):
        presence = torch.clamp(raw[:, :, 0], 0.0, 1.0)
        if _use_dense_structured_path():
            embedded = encoder(normalized)
            return embedded * presence.unsqueeze(2), presence

        batch = normalized.shape[0]
        slot_count = normalized.shape[1]
        flat_presence = presence.reshape(-1)
        active = torch.nonzero(flat_presence > 0.0, as_tuple=False).squeeze(1)
        output = normalized.new_zeros((batch * slot_count, output_size))
        if active.numel() > 0:
            flat = normalized.reshape(-1, normalized.shape[2])
            embedded = encoder(flat.index_select(0, active))
            embedded = embedded * flat_presence.index_select(
                0,
                active,
            ).unsqueeze(1)
            output = output.index_copy(0, active, embedded)
        return output.reshape(batch, slot_count, output_size), presence

    def forward(self, inputs):
        if self._fallback is not None:
            return self._fallback(inputs)
        if len(inputs) != 1:
            raise UnityTrainerException(
                "Bees structured policy expects exactly one vector observation."
            )

        raw = inputs[0]
        normalized = self.vector_input(raw)
        if normalized.shape[1] != BEES_OBSERVATION_SIZE:
            raise UnityTrainerException(
                "Bees structured policy received an unexpected observation size."
            )

        # Variable slots are already emitted as bounded semantic features by Unity.
        # Feed those raw values to the shared encoders so an identical entity or weapon
        # has identical encoder inputs regardless of which absolute slot it occupies.
        # The full-vector running normalizer remains useful for fixed-position global
        # state (self, capability, objectives and grids).
        self_obs = normalized[:, SELF_START : SELF_START + SELF_SIZE]
        self_embedding = self.self_encoder(self_obs)

        capability = self.capability_encoder(
            normalized[
                :,
                CAPABILITY_START : CAPABILITY_START + CAPABILITY_SIZE,
            ]
        )

        parent_norm = raw[:, PARENT_START : PARENT_START + PARENT_SIZE].reshape(
            -1,
            1,
            PARENT_SIZE,
        )
        parent_raw = raw[:, PARENT_START : PARENT_START + PARENT_SIZE].reshape(
            -1,
            1,
            PARENT_SIZE,
        )
        parent = self._encode_entities(parent_norm, parent_raw)[:, 0, :]

        ally_count = _slot_limit("allies", ALLY_COUNT)
        ally_norm = raw[
            :,
            ALLY_START : ALLY_START + ALLY_COUNT * ALLY_SIZE,
        ].reshape(-1, ALLY_COUNT, ALLY_SIZE)[:, :ally_count, :]
        ally_raw = raw[
            :,
            ALLY_START : ALLY_START + ALLY_COUNT * ALLY_SIZE,
        ].reshape(-1, ALLY_COUNT, ALLY_SIZE)[:, :ally_count, :]
        ally_embedding, ally_presence = self._encode_allies(
            ally_norm,
            ally_raw,
        )
        allies = self.ally_pool(
            self_embedding,
            ally_embedding,
            ally_presence,
        )

        enemy_count = _slot_limit("enemies", ENEMY_COUNT)
        enemy_norm = raw[
            :,
            ENEMY_START : ENEMY_START + ENEMY_COUNT * ENEMY_SIZE,
        ].reshape(-1, ENEMY_COUNT, ENEMY_SIZE)[:, :enemy_count, :]
        enemy_raw = raw[
            :,
            ENEMY_START : ENEMY_START + ENEMY_COUNT * ENEMY_SIZE,
        ].reshape(-1, ENEMY_COUNT, ENEMY_SIZE)[:, :enemy_count, :]
        enemy_embedding = self._encode_entities(enemy_norm, enemy_raw)
        enemy_presence = torch.clamp(enemy_raw[:, :, 0], 0.0, 1.0)
        enemies = self.enemy_pool(
            self_embedding,
            enemy_embedding,
            enemy_presence,
        )

        weapon_norm = raw[
            :,
            SELF_WEAPON_START :
            SELF_WEAPON_START + BEES_WEAPON_SLOTS * SELF_WEAPON_SIZE,
        ].reshape(-1, BEES_WEAPON_SLOTS, SELF_WEAPON_SIZE)
        weapon_raw = raw[
            :,
            SELF_WEAPON_START :
            SELF_WEAPON_START + BEES_WEAPON_SLOTS * SELF_WEAPON_SIZE,
        ].reshape(-1, BEES_WEAPON_SLOTS, SELF_WEAPON_SIZE)
        weapon_embeddings = self._encode_self_weapons(
            weapon_norm,
            weapon_raw,
        )
        weapon_presence = torch.clamp(weapon_raw[:, :, 0], 0.0, 1.0)
        weapon_pool = self.self_weapon_pool(
            self_embedding,
            weapon_embeddings,
            weapon_presence,
        )

        mining_count = _slot_limit("mining", MINING_COUNT)
        mining_norm = raw[
            :,
            MINING_START : MINING_START + MINING_COUNT * MINING_SIZE,
        ].reshape(-1, MINING_COUNT, MINING_SIZE)[:, :mining_count, :]
        mining_raw = raw[
            :,
            MINING_START : MINING_START + MINING_COUNT * MINING_SIZE,
        ].reshape(-1, MINING_COUNT, MINING_SIZE)[:, :mining_count, :]
        mining_entities, mining_presence = self._encode_set(
            mining_norm,
            mining_raw,
            self.mining_encoder,
            48,
        )
        mining = self.mining_pool(
            self_embedding,
            mining_entities,
            mining_presence,
        )

        map_count = _slot_limit("map_objects", MAP_OBJECT_COUNT)
        map_norm = raw[
            :,
            MAP_OBJECT_START :
            MAP_OBJECT_START + MAP_OBJECT_COUNT * MAP_OBJECT_SIZE,
        ].reshape(-1, MAP_OBJECT_COUNT, MAP_OBJECT_SIZE)[:, :map_count, :]
        map_raw = raw[
            :,
            MAP_OBJECT_START :
            MAP_OBJECT_START + MAP_OBJECT_COUNT * MAP_OBJECT_SIZE,
        ].reshape(-1, MAP_OBJECT_COUNT, MAP_OBJECT_SIZE)[:, :map_count, :]
        map_entities, map_presence = self._encode_set(
            map_norm,
            map_raw,
            self.map_object_encoder,
            64,
        )
        map_objects = self.map_object_pool(
            self_embedding,
            map_entities,
            map_presence,
        )

        collision_count = _slot_limit(
            "collisions",
            COLLISION_COUNT,
        )
        collision_norm = raw[
            :,
            COLLISION_START :
            COLLISION_START + COLLISION_COUNT * COLLISION_SIZE,
        ].reshape(-1, COLLISION_COUNT, COLLISION_SIZE)[:, :collision_count, :]
        collision_raw = raw[
            :,
            COLLISION_START :
            COLLISION_START + COLLISION_COUNT * COLLISION_SIZE,
        ].reshape(-1, COLLISION_COUNT, COLLISION_SIZE)[:, :collision_count, :]
        collision_entities, collision_presence = self._encode_set(
            collision_norm,
            collision_raw,
            self.collision_encoder,
            64,
        )
        collisions = self.collision_pool(
            self_embedding,
            collision_entities,
            collision_presence,
        )

        objective = self.objective_encoder(
            normalized[
                :,
                OBJECTIVE_START : OBJECTIVE_START + OBJECTIVE_SIZE,
            ]
        )
        navigation = self.navigation_encoder(
            normalized[
                :,
                NAVIGATION_START : NAVIGATION_START + NAVIGATION_SIZE,
            ]
        )
        exploration = self.exploration_encoder(
            normalized[
                :,
                EXPLORATION_START : EXPLORATION_START + EXPLORATION_SIZE,
            ]
        )
        tail = self.tail_encoder(
            torch.cat(
                [
                    normalized[
                        :,
                        EPISODE_PROGRESS_INDEX : EPISODE_PROGRESS_INDEX + 1,
                    ],
                    normalized[:, RESERVED_START:],
                ],
                dim=1,
            )
        )

        context = self.context_fuse(
            torch.cat(
                [
                    self_embedding,
                    capability,
                    parent,
                    allies,
                    enemies,
                    weapon_pool,
                    mining,
                    map_objects,
                    collisions,
                    objective,
                    navigation,
                    exploration,
                    tail,
                ],
                dim=1,
            )
        )

        faction = raw[:, FACTION_INDEX : FACTION_INDEX + 1]
        return torch.cat(
            [
                context,
                weapon_embeddings.reshape(
                    -1,
                    BEES_WEAPON_SLOTS * WEAPON_SLOT_EMBED,
                ),
                faction,
            ],
            dim=1,
        )


class BeesStructuredNetworkBody(nn.Module):
    """Independent Bee/Human actor encoders and trunks with within-faction slot sharing."""

    def __init__(
        self,
        observation_specs,
        network_settings,
        encoded_act_size: int = 0,
    ) -> None:
        super().__init__()
        self._bees = _is_bees_observation_specs(observation_specs)
        if not self._bees:
            self._fallback = _OriginalNetworkBody(
                observation_specs,
                network_settings,
                encoded_act_size=encoded_act_size,
            )
            self.normalize = self._fallback.normalize
            self.use_lstm = self._fallback.use_lstm
            self.h_size = self._fallback.h_size
            self.m_size = self._fallback.m_size
            self.observation_encoder = self._fallback.observation_encoder
            self.processors = self._fallback.processors
            return

        if encoded_act_size != 0:
            raise UnityTrainerException(
                "Bees structured actor does not support encoded action inputs."
            )
        if network_settings.memory is not None:
            raise UnityTrainerException(
                "Bees structured actor is feed-forward; recurrent memory is not part of policy ABI v22."
            )
        if int(network_settings.hidden_units) != FACTION_TRUNK_SIZE:
            raise UnityTrainerException(
                "Bees policy ABI v22 requires hidden_units=128."
            )

        self._fallback = None
        self.normalize = network_settings.normalize
        self.use_lstm = False
        self.h_size = int(network_settings.hidden_units)
        self.m_size = 0

        # Actor perception is faction-specific too. This prevents gradients from
        # Human tactical representations from rewriting Bee representations (and
        # vice versa) while still sharing encoders across entities/weapon slots
        # inside each faction.
        self.bee_observation_encoder = BeesStructuredObservationEncoder(
            observation_specs,
            self.h_size,
            network_settings.vis_encode_type,
            self.normalize,
        )
        self.human_observation_encoder = BeesStructuredObservationEncoder(
            observation_specs,
            self.h_size,
            network_settings.vis_encode_type,
            self.normalize,
        )
        self.processors = self.bee_observation_encoder.processors
        self.bee_trunk = LinearEncoder(
            CONTEXT_SIZE,
            network_settings.num_layers,
            self.h_size,
        )
        self.human_trunk = LinearEncoder(
            CONTEXT_SIZE,
            network_settings.num_layers,
            self.h_size,
        )

    @property
    def memory_size(self) -> int:
        if self._fallback is not None:
            return self._fallback.memory_size
        return 0

    def update_normalization(self, buffer) -> None:
        if self._fallback is not None:
            self._fallback.update_normalization(buffer)
            return

        obs = ObsUtil.from_buffer(buffer, 1)
        raw = obs[0].to_ndarray()
        if raw.shape[0] == 0:
            return
        bee_rows = raw[:, FACTION_INDEX] > 0.0
        human_rows = ~bee_rows
        if bee_rows.any():
            bee_vector_input = self.bee_observation_encoder.vector_input
            bee_values = torch.as_tensor(raw[bee_rows])
            if bee_vector_input.normalizer is not None:
                bee_values = bee_values.to(
                    device=bee_vector_input.normalizer.running_mean.device,
                    dtype=bee_vector_input.normalizer.running_mean.dtype,
                )
            bee_vector_input.update_normalization(bee_values)
        if human_rows.any():
            human_vector_input = self.human_observation_encoder.vector_input
            human_values = torch.as_tensor(raw[human_rows])
            if human_vector_input.normalizer is not None:
                human_values = human_values.to(
                    device=human_vector_input.normalizer.running_mean.device,
                    dtype=human_vector_input.normalizer.running_mean.dtype,
                )
            human_vector_input.update_normalization(human_values)

    def copy_normalization(self, other_network) -> None:
        if self._fallback is not None:
            target = (
                other_network._fallback
                if isinstance(other_network, BeesStructuredNetworkBody)
                else other_network
            )
            self._fallback.copy_normalization(target)
            return
        if (
            isinstance(other_network, BeesStructuredNetworkBody)
            and other_network._fallback is None
        ):
            self.bee_observation_encoder.copy_normalization(
                other_network.bee_observation_encoder
            )
            self.human_observation_encoder.copy_normalization(
                other_network.human_observation_encoder
            )

    def forward(
        self,
        inputs,
        actions: Optional[torch.Tensor] = None,
        memories: Optional[torch.Tensor] = None,
        sequence_length: int = 1,
    ):
        if self._fallback is not None:
            return self._fallback(
                inputs,
                actions=actions,
                memories=memories,
                sequence_length=sequence_length,
            )

        raw_faction = inputs[0][:, FACTION_INDEX : FACTION_INDEX + 1]
        bee_weight = torch.clamp((raw_faction + 1.0) * 0.5, 0.0, 1.0)
        if _use_dense_structured_path():
            bee_structured = self.bee_observation_encoder(inputs)
            human_structured = self.human_observation_encoder(inputs)
            structured = (
                bee_structured * bee_weight
                + human_structured * (1.0 - bee_weight)
            )
            context = structured[:, :CONTEXT_SIZE]
            weapon_start = CONTEXT_SIZE
            weapon_end = (
                weapon_start + BEES_WEAPON_SLOTS * WEAPON_SLOT_EMBED
            )
            weapon_embeddings = structured[:, weapon_start:weapon_end]
            bee_encoding = self.bee_trunk(context)
            human_encoding = self.human_trunk(context)
            faction_encoding = (
                bee_encoding * bee_weight
                + human_encoding * (1.0 - bee_weight)
            )
            return (
                torch.cat(
                    [
                        faction_encoding,
                        weapon_embeddings,
                        raw_faction,
                    ],
                    dim=1,
                ),
                memories,
            )

        batch_size = raw_faction.shape[0]
        output = inputs[0].new_zeros((batch_size, ACTION_ENCODING_SIZE))
        weapon_start = CONTEXT_SIZE
        weapon_end = weapon_start + BEES_WEAPON_SLOTS * WEAPON_SLOT_EMBED

        def encode_rows(indices, observation_encoder, trunk):
            subset_inputs = [
                value.index_select(0, indices)
                for value in inputs
            ]
            structured = observation_encoder(subset_inputs)
            context = structured[:, :CONTEXT_SIZE]
            weapons = structured[:, weapon_start:weapon_end]
            faction_encoding = trunk(context)
            faction = raw_faction.index_select(0, indices)
            return torch.cat(
                [faction_encoding, weapons, faction],
                dim=1,
            )

        pure_bee = torch.nonzero(
            bee_weight[:, 0] >= 1.0,
            as_tuple=False,
        ).squeeze(1)
        pure_human = torch.nonzero(
            bee_weight[:, 0] <= 0.0,
            as_tuple=False,
        ).squeeze(1)
        mixed = torch.nonzero(
            (bee_weight[:, 0] > 0.0) & (bee_weight[:, 0] < 1.0),
            as_tuple=False,
        ).squeeze(1)

        if pure_bee.numel() > 0:
            output = output.index_copy(
                0,
                pure_bee,
                encode_rows(
                    pure_bee,
                    self.bee_observation_encoder,
                    self.bee_trunk,
                ),
            )
        if pure_human.numel() > 0:
            output = output.index_copy(
                0,
                pure_human,
                encode_rows(
                    pure_human,
                    self.human_observation_encoder,
                    self.human_trunk,
                ),
            )
        if mixed.numel() > 0:
            subset_inputs = [
                value.index_select(0, mixed)
                for value in inputs
            ]
            weights = bee_weight.index_select(0, mixed)
            bee_structured = self.bee_observation_encoder(subset_inputs)
            human_structured = self.human_observation_encoder(subset_inputs)
            structured = (
                bee_structured * weights
                + human_structured * (1.0 - weights)
            )
            context = structured[:, :CONTEXT_SIZE]
            weapons = structured[:, weapon_start:weapon_end]
            bee_encoding = self.bee_trunk(context)
            human_encoding = self.human_trunk(context)
            faction_encoding = (
                bee_encoding * weights
                + human_encoding * (1.0 - weights)
            )
            faction = raw_faction.index_select(0, mixed)
            output = output.index_copy(
                0,
                mixed,
                torch.cat(
                    [faction_encoding, weapons, faction],
                    dim=1,
                ),
            )

        return output, memories


class BeesStructuredActionModel(_OriginalActionModel):
    """Faction-specific action heads with one weapon head reused for every slot."""

    def __init__(
        self,
        hidden_size: int,
        action_spec,
        conditional_sigma: bool = False,
        tanh_squash: bool = False,
        deterministic: bool = False,
    ) -> None:
        if not _is_bees_action_spec(action_spec):
            super().__init__(
                hidden_size,
                action_spec,
                conditional_sigma=conditional_sigma,
                tanh_squash=tanh_squash,
                deterministic=deterministic,
            )
            self._bees = False
            return

        nn.Module.__init__(self)
        self._bees = True
        self.encoding_size = hidden_size
        self.action_spec = action_spec
        self.clip_action = not tanh_squash
        self._deterministic = deterministic

        weapon_action_input = FACTION_TRUNK_SIZE + WEAPON_SLOT_EMBED

        self.bee_movement = GaussianDistribution(
            FACTION_TRUNK_SIZE,
            2,
            conditional_sigma=conditional_sigma,
            tanh_squash=tanh_squash,
        )
        self.human_movement = GaussianDistribution(
            FACTION_TRUNK_SIZE,
            2,
            conditional_sigma=conditional_sigma,
            tanh_squash=tanh_squash,
        )
        self.bee_communication = GaussianDistribution(
            FACTION_TRUNK_SIZE,
            4,
            conditional_sigma=conditional_sigma,
            tanh_squash=tanh_squash,
        )
        self.human_communication = GaussianDistribution(
            FACTION_TRUNK_SIZE,
            4,
            conditional_sigma=conditional_sigma,
            tanh_squash=tanh_squash,
        )
        self.bee_weapon_aim = GaussianDistribution(
            weapon_action_input,
            2,
            conditional_sigma=conditional_sigma,
            tanh_squash=tanh_squash,
        )
        self.human_weapon_aim = GaussianDistribution(
            weapon_action_input,
            2,
            conditional_sigma=conditional_sigma,
            tanh_squash=tanh_squash,
        )

        self.bee_weapon_fire = linear_layer(
            weapon_action_input,
            2,
            kernel_init=Initialization.KaimingHeNormal,
            kernel_gain=0.1,
            bias_init=Initialization.Zero,
        )
        self.human_weapon_fire = linear_layer(
            weapon_action_input,
            2,
            kernel_init=Initialization.KaimingHeNormal,
            kernel_gain=0.1,
            bias_init=Initialization.Zero,
        )
        self.bee_special = linear_layer(
            FACTION_TRUNK_SIZE,
            5,
            kernel_init=Initialization.KaimingHeNormal,
            kernel_gain=0.1,
            bias_init=Initialization.Zero,
        )
        self.human_special = linear_layer(
            FACTION_TRUNK_SIZE,
            5,
            kernel_init=Initialization.KaimingHeNormal,
            kernel_gain=0.1,
            bias_init=Initialization.Zero,
        )

    @staticmethod
    def _mix(
        bee: torch.Tensor,
        human: torch.Tensor,
        bee_mask: torch.Tensor,
    ) -> torch.Tensor:
        return bee * bee_mask + human * (1.0 - bee_mask)

    @staticmethod
    def _masked_logits(
        logits: torch.Tensor,
        allow_mask: torch.Tensor,
    ) -> torch.Tensor:
        block_mask = -1.0 * allow_mask + 1.0
        return logits * allow_mask - 1.0e8 * block_mask

    def _get_dists(self, inputs: torch.Tensor, masks: torch.Tensor):
        if not self._bees:
            return super()._get_dists(inputs, masks)

        global_encoding = inputs[:, :FACTION_TRUNK_SIZE]
        weapon_start = FACTION_TRUNK_SIZE
        weapon_end = weapon_start + BEES_WEAPON_SLOTS * WEAPON_SLOT_EMBED
        weapon_embeddings = inputs[:, weapon_start:weapon_end].reshape(
            -1,
            BEES_WEAPON_SLOTS,
            WEAPON_SLOT_EMBED,
        )
        faction = inputs[:, weapon_end : weapon_end + 1]
        bee_mask = torch.clamp((faction + 1.0) * 0.5, 0.0, 1.0)

        if masks is None:
            masks = torch.ones(
                (
                    inputs.shape[0],
                    sum(BEES_DISCRETE_BRANCHES),
                ),
                dtype=inputs.dtype,
                device=inputs.device,
            )

        bee_movement = self.bee_movement(global_encoding)
        human_movement = self.human_movement(global_encoding)
        movement_mean = self._mix(
            bee_movement.mean,
            human_movement.mean,
            bee_mask,
        )
        movement_std = self._mix(
            bee_movement.std,
            human_movement.std,
            bee_mask,
        )

        weapon_means = []
        weapon_stds = []
        discrete = []
        mask_offset = 0
        for slot in range(BEES_WEAPON_SLOTS):
            weapon_input = torch.cat(
                [global_encoding, weapon_embeddings[:, slot, :]],
                dim=1,
            )
            bee_aim = self.bee_weapon_aim(weapon_input)
            human_aim = self.human_weapon_aim(weapon_input)
            weapon_means.append(
                self._mix(bee_aim.mean, human_aim.mean, bee_mask)
            )
            weapon_stds.append(
                self._mix(bee_aim.std, human_aim.std, bee_mask)
            )

            bee_fire = self.bee_weapon_fire(weapon_input)
            human_fire = self.human_weapon_fire(weapon_input)
            fire_logits = self._mix(
                bee_fire,
                human_fire,
                bee_mask,
            )
            branch_size = BEES_DISCRETE_BRANCHES[slot]
            allow = masks[:, mask_offset : mask_offset + branch_size]
            mask_offset += branch_size
            discrete.append(
                CategoricalDistInstance(
                    self._masked_logits(fire_logits, allow)
                )
            )

        bee_communication = self.bee_communication(global_encoding)
        human_communication = self.human_communication(global_encoding)
        communication_mean = self._mix(
            bee_communication.mean,
            human_communication.mean,
            bee_mask,
        )
        communication_std = self._mix(
            bee_communication.std,
            human_communication.std,
            bee_mask,
        )

        continuous = GaussianDistInstance(
            torch.cat(
                [movement_mean, *weapon_means, communication_mean],
                dim=1,
            ),
            torch.cat(
                [movement_std, *weapon_stds, communication_std],
                dim=1,
            ),
        )

        bee_special = self.bee_special(global_encoding)
        human_special = self.human_special(global_encoding)
        special_logits = self._mix(
            bee_special,
            human_special,
            bee_mask,
        )
        special_size = BEES_DISCRETE_BRANCHES[-1]
        special_allow = masks[
            :,
            mask_offset : mask_offset + special_size,
        ]
        discrete.append(
            CategoricalDistInstance(
                self._masked_logits(special_logits, special_allow)
            )
        )
        return DistInstances(continuous, discrete)


def install_structured_policy():
    """Install the exact-shape Bees structured actor/encoder patch."""

    global _INSTALLED_STATE
    if _INSTALLED_STATE is not None:
        return None

    import mlagents.trainers.torch_entities.action_model as action_model_module
    import mlagents.trainers.torch_entities.networks as networks_module

    state = (
        networks_module.ObservationEncoder,
        networks_module.NetworkBody,
        networks_module.ActionModel,
        action_model_module.ActionModel,
    )
    networks_module.ObservationEncoder = BeesStructuredObservationEncoder
    networks_module.NetworkBody = BeesStructuredNetworkBody
    networks_module.ActionModel = BeesStructuredActionModel
    action_model_module.ActionModel = BeesStructuredActionModel
    _INSTALLED_STATE = state
    return state


def restore_structured_policy(state=None) -> None:
    """Restore upstream ML-Agents network classes."""

    global _INSTALLED_STATE
    installed = _INSTALLED_STATE
    if installed is None:
        return
    originals = state or installed

    import mlagents.trainers.torch_entities.action_model as action_model_module
    import mlagents.trainers.torch_entities.networks as networks_module

    (
        networks_module.ObservationEncoder,
        networks_module.NetworkBody,
        networks_module.ActionModel,
        action_model_module.ActionModel,
    ) = originals
    _INSTALLED_STATE = None
