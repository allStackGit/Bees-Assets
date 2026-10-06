"""Bees-specific factorized MA-POCA critic.

ML-Agents' stock POCA critic re-encodes every group member's complete observation
for both the centralized value and counterfactual baseline passes. Bees observations
already contain fleet/world context, so that generic dataflow multiplies a 7,743-value
structured observation by every teammate.

This critic keeps the POCA interfaces expected by TorchPOCAOptimizer but factors the
state into:
* one full structured encoding of the focal agent;
* compact per-groupmate local state (self, capability, parent, weapons);
* the groupmate's local navigation grid; and
* groupmate actions only for the counterfactual baseline.

The actor and exported policy are unchanged.
"""

from __future__ import annotations

from typing import Dict, List, Optional, Sequence, Tuple

from mlagents.torch_utils import default_device, nn, torch
from mlagents.trainers.buffer import AgentBuffer
from mlagents.trainers.poca.optimizer_torch import TorchPOCAOptimizer
from mlagents.trainers.torch_entities.agent_action import AgentAction
from mlagents.trainers.torch_entities.decoders import ValueHeads

from bees_mlagents_structured_policy import (
    BEES_CONTINUOUS_ACTIONS,
    BEES_DISCRETE_BRANCHES,
    BEES_OBSERVATION_SIZE,
    BEES_WEAPON_SLOTS,
    CAPABILITY_SIZE,
    CAPABILITY_START,
    NAVIGATION_SIZE,
    NAVIGATION_START,
    PARENT_SIZE,
    PARENT_START,
    SELF_SIZE,
    SELF_START,
    SELF_WEAPON_SIZE,
    SELF_WEAPON_START,
    BeesStructuredObservationEncoder,
)


GROUP_STATE_WIDTH = (
    SELF_SIZE
    + CAPABILITY_SIZE
    + PARENT_SIZE
    + BEES_WEAPON_SLOTS * SELF_WEAPON_SIZE
)
GROUP_ACTION_WIDTH = BEES_CONTINUOUS_ACTIONS + sum(BEES_DISCRETE_BRANCHES)
CRITIC_WIDTH = 128
GROUP_NAV_WIDTH = 64
GROUP_STATE_EMBED = 128
GROUP_ACTION_EMBED = 64

_ORIGINAL_POCA_INIT = None


def _mlp(input_size: int, hidden_size: int, output_size: int) -> nn.Sequential:
    return nn.Sequential(
        nn.Linear(input_size, hidden_size),
        nn.LeakyReLU(),
        nn.Linear(hidden_size, output_size),
        nn.LeakyReLU(),
    )


def _is_bees_policy(policy) -> bool:
    behavior_spec = getattr(policy, "behavior_spec", None)
    if behavior_spec is None:
        return False
    observation_specs = getattr(behavior_spec, "observation_specs", ())
    action_spec = getattr(behavior_spec, "action_spec", None)
    return (
        len(observation_specs) == 1
        and tuple(observation_specs[0].shape) == (BEES_OBSERVATION_SIZE,)
        and action_spec is not None
        and int(action_spec.continuous_size) == BEES_CONTINUOUS_ACTIONS
        and tuple(int(value) for value in action_spec.discrete_branches)
        == BEES_DISCRETE_BRANCHES
    )


class BeesFactorizedPOCACritic(nn.Module):
    """POCA critic that avoids full-observation encoding for every groupmate."""

    def __init__(
        self,
        stream_names: Sequence[str],
        observation_specs,
        network_settings,
        action_spec,
    ) -> None:
        super().__init__()
        if (
            len(observation_specs) != 1
            or tuple(observation_specs[0].shape) != (BEES_OBSERVATION_SIZE,)
        ):
            raise ValueError("Bees factorized POCA critic requires the Bees observation ABI.")
        if (
            int(action_spec.continuous_size) != BEES_CONTINUOUS_ACTIONS
            or tuple(int(value) for value in action_spec.discrete_branches)
            != BEES_DISCRETE_BRANCHES
        ):
            raise ValueError("Bees factorized POCA critic requires the Bees action ABI.")
        if network_settings.memory is not None:
            raise ValueError("Bees factorized POCA critic is feed-forward.")

        self.action_spec = action_spec
        self.observation_encoder = BeesStructuredObservationEncoder(
            observation_specs,
            int(network_settings.hidden_units),
            network_settings.vis_encode_type,
            bool(network_settings.normalize),
        )
        focal_width = int(self.observation_encoder.total_enc_size)

        self.focal_encoder = _mlp(focal_width, 256, CRITIC_WIDTH)
        self.group_state_encoder = _mlp(
            GROUP_STATE_WIDTH,
            GROUP_STATE_EMBED,
            GROUP_STATE_EMBED,
        )
        self.group_navigation_encoder = _mlp(
            NAVIGATION_SIZE,
            128,
            GROUP_NAV_WIDTH,
        )
        self.group_state_fuse = _mlp(
            GROUP_STATE_EMBED + GROUP_NAV_WIDTH,
            CRITIC_WIDTH,
            CRITIC_WIDTH,
        )
        self.group_action_encoder = _mlp(
            GROUP_ACTION_WIDTH,
            GROUP_ACTION_EMBED,
            GROUP_ACTION_EMBED,
        )
        self.group_action_fuse = _mlp(
            CRITIC_WIDTH + GROUP_ACTION_EMBED,
            CRITIC_WIDTH,
            CRITIC_WIDTH,
        )
        self.group_state_score = nn.Linear(CRITIC_WIDTH, 1)
        self.group_action_score = nn.Linear(CRITIC_WIDTH, 1)
        self.value_fuse = _mlp(
            CRITIC_WIDTH * 2 + 1,
            CRITIC_WIDTH,
            CRITIC_WIDTH,
        )
        self.baseline_fuse = _mlp(
            CRITIC_WIDTH * 2 + 1,
            CRITIC_WIDTH,
            CRITIC_WIDTH,
        )

        # Use a distinct module name from stock POCA's "value_heads". Old critic
        # checkpoints therefore cannot partially populate this architecture.
        self.factorized_value_heads = ValueHeads(
            list(stream_names),
            CRITIC_WIDTH,
            1,
        )
        self._shared_cache = None

    @property
    def memory_size(self) -> int:
        return 0

    def update_normalization(self, buffer: AgentBuffer) -> None:
        self.observation_encoder.update_normalization(buffer)

    @staticmethod
    def _compact_group_state(\n        raw: torch.Tensor,\n    ) -> Tuple[torch.Tensor, torch.Tensor, torch.Tensor]:
        valid = torch.isfinite(raw[:, SELF_START])
        clean = torch.nan_to_num(raw, nan=0.0, posinf=0.0, neginf=0.0)
        compact = torch.cat(
            [
                clean[:, SELF_START : SELF_START + SELF_SIZE],
                clean[
                    :,
                    CAPABILITY_START : CAPABILITY_START + CAPABILITY_SIZE,
                ],
                clean[:, PARENT_START : PARENT_START + PARENT_SIZE],
                clean[
                    :,
                    SELF_WEAPON_START
                    : SELF_WEAPON_START + BEES_WEAPON_SLOTS * SELF_WEAPON_SIZE,
                ],
            ],
            dim=1,
        )
        navigation = clean[
            :,
            NAVIGATION_START : NAVIGATION_START + NAVIGATION_SIZE,
        ]
        return compact, navigation, valid

    @staticmethod
    def _masked_pool(
        tokens: torch.Tensor,
        valid: torch.Tensor,
        scorer: nn.Module,
    ) -> torch.Tensor:
        if int(tokens.shape[1]) == 0:
            return tokens.new_zeros((tokens.shape[0], tokens.shape[2]))
        mask = valid.to(dtype=tokens.dtype)
        logits = scorer(tokens).squeeze(2)
        logits = logits + (1.0 - mask) * -10000.0
        weights = torch.softmax(logits, dim=1) * mask
        weights = weights / torch.clamp(
            weights.sum(dim=1, keepdim=True),
            min=1.0e-6,
        )
        return torch.sum(tokens * weights.unsqueeze(2), dim=1)

    def _encode_group_states(
        self,
        groupmate_obs,
        batch_size: int,
        reference: torch.Tensor,
    ) -> Tuple[torch.Tensor, torch.Tensor, torch.Tensor]:
        if not groupmate_obs:
            empty_tokens = reference.new_zeros((batch_size, 0, CRITIC_WIDTH))
            empty_valid = torch.zeros(
                (batch_size, 0),
                dtype=torch.bool,
                device=reference.device,
            )
            empty_count = reference.new_zeros((batch_size, 1))
            return empty_tokens, empty_valid, empty_count

        compact_parts = []
        navigation_parts = []
        valid_parts = []
        for member in groupmate_obs:
            if len(member) != 1:
                raise RuntimeError(
                    "Bees factorized POCA critic expects one vector observation per groupmate."
                )
            raw = member[0]
            if int(raw.shape[0]) != batch_size:
                raise RuntimeError(
                    "Bees factorized POCA groupmate batch size changed unexpectedly."
                )
            compact, navigation, valid = self._compact_group_state(raw)
            compact_parts.append(compact)
            navigation_parts.append(navigation)
            valid_parts.append(valid)

        compact_tensor = torch.stack(compact_parts, dim=1)
        navigation_tensor = torch.stack(navigation_parts, dim=1)
        valid = torch.stack(valid_parts, dim=1)

        group_count = int(compact_tensor.shape[1])
        state = self.group_state_encoder(
            compact_tensor.reshape(batch_size * group_count, GROUP_STATE_WIDTH)
        )
        navigation = self.group_navigation_encoder(
            navigation_tensor.reshape(batch_size * group_count, NAVIGATION_SIZE)
        )
        tokens = self.group_state_fuse(
            torch.cat([state, navigation], dim=1)
        ).reshape(batch_size, group_count, CRITIC_WIDTH)
        tokens = tokens * valid.to(tokens.dtype).unsqueeze(2)
        count = valid.to(tokens.dtype).sum(dim=1, keepdim=True) / 64.0
        return tokens, valid, count

    def _flatten_group_actions(
        self,
        actions: List[AgentAction],
        batch_size: int,
        group_count: int,
        reference: torch.Tensor,
    ) -> torch.Tensor:
        if group_count == 0:
            return reference.new_zeros((batch_size, 0, GROUP_ACTION_WIDTH))
        if len(actions) != group_count:
            raise RuntimeError(
                "Bees factorized POCA groupmate action count does not match group observations."
            )

        continuous = torch.stack(
            [action.continuous_tensor for action in actions],
            dim=1,
        )
        discrete_one_hot = []
        for branch_index, branch_size in enumerate(BEES_DISCRETE_BRANCHES):
            branch_actions = torch.stack(
                [
                    action.discrete_list[branch_index]
                    for action in actions
                ],
                dim=1,
            ).reshape(batch_size, group_count)
            discrete_one_hot.append(
                torch.nn.functional.one_hot(
                    branch_actions.long(),
                    int(branch_size),
                ).to(dtype=continuous.dtype)
            )
        return torch.cat(
            [
                continuous,
                torch.cat(discrete_one_hot, dim=2),
            ],
            dim=2,
        )

    @staticmethod
    def _cache_key(current_obs, groupmate_obs) -> Tuple[int, Tuple[int, ...]]:
        return (
            id(current_obs[0]),
            tuple(id(member[0]) for member in groupmate_obs),
        )

    def _shared_state(self, current_obs, groupmate_obs):
        if len(current_obs) != 1:
            raise RuntimeError(
                "Bees factorized POCA critic expects one focal vector observation."
            )
        key = self._cache_key(current_obs, groupmate_obs)
        cached = self._shared_cache
        if cached is not None and cached["key"] == key:
            return cached

        raw = current_obs[0]
        batch_size = int(raw.shape[0])
        focal = self.focal_encoder(self.observation_encoder(current_obs))
        group_tokens, valid, count = self._encode_group_states(
            groupmate_obs,
            batch_size,
            raw,
        )
        state_pool = self._masked_pool(
            group_tokens,
            valid,
            self.group_state_score,
        )
        cached = {
            "key": key,
            "focal": focal,
            "group_tokens": group_tokens,
            "valid": valid,
            "count": count,
            "state_pool": state_pool,
        }
        self._shared_cache = cached
        return cached

    def critic_pass(
        self,
        obs,
        memories: Optional[torch.Tensor] = None,
        sequence_length: int = 1,
    ) -> Tuple[Dict[str, torch.Tensor], Optional[torch.Tensor]]:
        if memories is not None and not isinstance(memories, list):
            if getattr(memories, "numel", lambda: 0)() > 0:
                raise RuntimeError("Bees factorized POCA critic does not use memory.")
        if not obs:
            raise RuntimeError("Bees factorized POCA critic received no observations.")

        current_obs = obs[0]
        groupmate_obs = obs[1:]
        shared = self._shared_state(current_obs, groupmate_obs)
        value_encoding = self.value_fuse(
            torch.cat(
                [
                    shared["focal"],
                    shared["state_pool"],
                    shared["count"],
                ],
                dim=1,
            )
        )
        return self.factorized_value_heads(value_encoding), memories

    def baseline(
        self,
        obs_without_actions,
        obs_with_actions,
        memories: Optional[torch.Tensor] = None,
        sequence_length: int = 1,
    ) -> Tuple[Dict[str, torch.Tensor], Optional[torch.Tensor]]:
        groupmate_obs, groupmate_actions = obs_with_actions
        shared = self._shared_state(obs_without_actions, groupmate_obs)
        group_tokens = shared["group_tokens"]
        batch_size = int(shared["focal"].shape[0])
        group_count = int(group_tokens.shape[1])

        flat_actions = self._flatten_group_actions(
            groupmate_actions,
            batch_size,
            group_count,
            shared["focal"],
        )
        if group_count == 0:
            action_pool = shared["focal"].new_zeros(
                (batch_size, CRITIC_WIDTH)
            )
        else:
            action_embedding = self.group_action_encoder(
                flat_actions.reshape(
                    batch_size * group_count,
                    GROUP_ACTION_WIDTH,
                )
            ).reshape(
                batch_size,
                group_count,
                GROUP_ACTION_EMBED,
            )
            action_tokens = self.group_action_fuse(
                torch.cat(
                    [group_tokens, action_embedding],
                    dim=2,
                ).reshape(
                    batch_size * group_count,
                    CRITIC_WIDTH + GROUP_ACTION_EMBED,
                )
            ).reshape(batch_size, group_count, CRITIC_WIDTH)
            action_tokens = (
                action_tokens
                * shared["valid"].to(action_tokens.dtype).unsqueeze(2)
            )
            action_pool = self._masked_pool(
                action_tokens,
                shared["valid"],
                self.group_action_score,
            )

        baseline_encoding = self.baseline_fuse(
            torch.cat(
                [
                    shared["focal"],
                    action_pool,
                    shared["count"],
                ],
                dim=1,
            )
        )
        output = self.factorized_value_heads(baseline_encoding)
        # Value and baseline are called consecutively for a minibatch. Release the
        # graph references after baseline so the next minibatch cannot retain them.
        self._shared_cache = None
        return output, memories


def compact_group_observation_numpy(raw):
    """Project one full Bees groupmate observation onto critic-only local state."""

    import numpy as np

    values = np.asarray(raw, dtype=np.float32)
    if values.ndim != 1 or int(values.shape[0]) != BEES_OBSERVATION_SIZE:
        raise ValueError(
            "Bees compact group observation requires one full Bees observation."
        )
    return np.concatenate(
        [
            values[SELF_START : SELF_START + SELF_SIZE],
            values[
                CAPABILITY_START : CAPABILITY_START + CAPABILITY_SIZE
            ],
            values[PARENT_START : PARENT_START + PARENT_SIZE],
            values[
                SELF_WEAPON_START
                : SELF_WEAPON_START + BEES_WEAPON_SLOTS * SELF_WEAPON_SIZE
            ],
            values[
                NAVIGATION_START : NAVIGATION_START + NAVIGATION_SIZE
            ],
        ]
    ).astype(np.float32, copy=False)


def install_factorized_poca_critic():
    """Install the Bees factorized critic before ML-Agents creates its optimizer."""

    global _ORIGINAL_POCA_INIT
    if _ORIGINAL_POCA_INIT is not None:
        return _ORIGINAL_POCA_INIT

    original = TorchPOCAOptimizer.__init__

    def factorized_init(self, policy, trainer_settings):
        original(self, policy, trainer_settings)
        if not _is_bees_policy(policy):
            return

        critic = BeesFactorizedPOCACritic(
            list(self.stream_names),
            policy.behavior_spec.observation_specs,
            trainer_settings.network_settings,
            policy.behavior_spec.action_spec,
        )
        critic.to(default_device())
        self._critic = critic

        # Two parameter groups intentionally make legacy one-group Adam
        # checkpoints incompatible. ML-Agents then initializes Adam cleanly while
        # loading the actor module independently from the same checkpoint.
        actor_parameters = [
            parameter
            for parameter in self.policy.actor.parameters()
            if parameter.requires_grad
        ]
        critic_parameters = [
            parameter
            for parameter in self._critic.parameters()
            if parameter.requires_grad
        ]
        self.optimizer = torch.optim.Adam(
            [
                {"params": actor_parameters},
                {"params": critic_parameters},
            ],
            lr=trainer_settings.hyperparameters.learning_rate,
        )
        self._bees_factorized_poca = True

    TorchPOCAOptimizer.__init__ = factorized_init
    _ORIGINAL_POCA_INIT = original
    return original


def restore_factorized_poca_critic(original=None) -> None:
    global _ORIGINAL_POCA_INIT
    target = original if original is not None else _ORIGINAL_POCA_INIT
    if target is None:
        return
    TorchPOCAOptimizer.__init__ = target
    _ORIGINAL_POCA_INIT = None
