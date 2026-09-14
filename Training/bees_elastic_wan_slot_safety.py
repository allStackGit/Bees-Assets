"""Exclusive actor-slot ownership for elastic WAN rollout machines."""

from __future__ import annotations

from typing import Any, Mapping, Type

import bees_elastic_wan_training as elastic


class SlotSafeElasticWanBroker(elastic.ElasticWanBroker):
    def register_actor(self, payload: Mapping[str, Any]) -> None:
        actor_id = self._validate_actor_id(payload.get("actor_id"))
        instance_id = payload.get("actor_instance_id")
        if not isinstance(instance_id, str) or len(instance_id) < 16 or len(instance_id) > 128:
            raise ValueError("elastic WAN actor registration requires a stable actor_instance_id")

        with self._condition:
            self._active_snapshot_locked()
            previous = self._registrations.get(actor_id)
            if previous is not None:
                previous_instance = previous.get("actor_instance_id")
                if previous_instance is not None and previous_instance != instance_id:
                    raise ValueError(
                        f"actor slot {actor_id} is already owned by another live remote process; "
                        "use a different --actor-id or wait for its lease to expire"
                    )
            super().register_actor(payload)
            self._registrations[actor_id]["actor_instance_id"] = instance_id


def install_slot_safety() -> Type[elastic.ElasticWanBroker]:
    original = elastic.ElasticWanBroker
    elastic.ElasticWanBroker = SlotSafeElasticWanBroker
    return original


def restore_slot_safety(original: Type[elastic.ElasticWanBroker]) -> None:
    elastic.ElasticWanBroker = original
