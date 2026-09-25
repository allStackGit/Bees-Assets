"""Exclusive actor-slot ownership, liveness, and local ABI authority for elastic WAN actors."""

from __future__ import annotations

import time
from typing import Any, Mapping, Type

import bees_elastic_wan_training as elastic
import bees_wan_actor_training as wan


class SlotSafeElasticWanBroker(elastic.ElasticWanBroker):
    def set_reference_behavior_specs(self, behavior_specs: Mapping[str, Any]) -> None:
        """Pin Exeter's local behavior specs and reject an early incompatible remote registration."""
        if not behavior_specs:
            raise RuntimeError("Local Exeter environments expose no trainable behaviors")
        local_signatures = {
            str(name): wan._behavior_spec_signature(spec)
            for name, spec in behavior_specs.items()
        }
        with self._condition:
            self._active_snapshot_locked()
            for actor_id, record in self._registrations.items():
                if record.get("signatures") != local_signatures:
                    raise RuntimeError(
                        f"remote actor {actor_id} registered behavior specifications that differ "
                        "from Exeter's local training environments"
                    )
        super().set_reference_behavior_specs(behavior_specs)

    @staticmethod
    def _payload_instance_id(payload: Mapping[str, Any]) -> str:
        instance_id = payload.get("actor_instance_id")
        if not isinstance(instance_id, str) or len(instance_id) < 16 or len(instance_id) > 128:
            raise ValueError("elastic WAN request requires a stable actor_instance_id")
        return instance_id

    def _require_live_instance(self, payload: Mapping[str, Any]) -> int:
        actor_id = self._validate_actor_id(payload.get("actor_id"))
        instance_id = self._payload_instance_id(payload)
        with self._condition:
            self._active_snapshot_locked()
            record = self._registrations.get(actor_id)
            if record is None:
                raise ValueError("actor lease expired; re-register before continuing")
            if record.get("actor_instance_id") != instance_id:
                raise ValueError(
                    f"actor slot {actor_id} is owned by another live remote process"
                )
        return actor_id

    def register_actor(self, payload: Mapping[str, Any]) -> None:
        actor_id = self._validate_actor_id(payload.get("actor_id"))
        instance_id = self._payload_instance_id(payload)

        with self._condition:
            self._active_snapshot_locked()
            previous = self._registrations.get(actor_id)
            if previous is not None:
                previous_instance = previous.get("actor_instance_id")
                replacement_claim = None
                actor_key = payload.get("actor_key")
                if isinstance(actor_key, str) and actor_key:
                    replacement_claim = self._claims.get(actor_key)
                claimed_replacement = (
                    replacement_claim is not None
                    and int(replacement_claim.get("actor_id", -1)) == actor_id
                    and replacement_claim.get("actor_instance_id") == instance_id
                    and previous.get("actor_key") == actor_key
                )
                if (
                    previous_instance is not None
                    and previous_instance != instance_id
                    and not claimed_replacement
                ):
                    raise ValueError(
                        f"actor slot {actor_id} is already owned by another live remote process; "
                        "use a different --actor-id or obtain a replacement claim"
                    )
            super().register_actor(payload)
            self._registrations[actor_id]["actor_instance_id"] = instance_id

    def submit_trajectory_batch(self, payload: Mapping[str, Any]) -> int:
        self._require_live_instance(payload)
        return super().submit_trajectory_batch(payload)

    def acknowledge_reset(self, payload: Mapping[str, Any]) -> None:
        """Treat the authenticated control acknowledgement as an actor heartbeat too."""
        actor_id = self._require_live_instance(payload)
        super().acknowledge_reset(payload)
        with self._condition:
            record = self._registrations.get(actor_id)
            if record is None:
                raise ValueError("actor lease expired; re-register before sending heartbeat")
            record["last_seen"] = time.monotonic()
            self._condition.notify_all()


def install_slot_safety() -> Type[elastic.ElasticWanBroker]:
    original = elastic.ElasticWanBroker
    elastic.ElasticWanBroker = SlotSafeElasticWanBroker
    return original


def restore_slot_safety(original: Type[elastic.ElasticWanBroker]) -> None:
    elastic.ElasticWanBroker = original
