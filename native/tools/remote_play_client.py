#!/usr/bin/env python3
"""Control an existing native Bees battle over its authenticated co-op socket.

This is a terminal client for the real native host, not a second simulator.
The local game and this client share one player side and command authority.
Requires BEES_NATIVE_REMOTE_TOKEN to match the server's environment.
"""
import argparse
import json
import math
import os
from queue import SimpleQueue
import shlex
import socket
import threading


def remote_token(side, spectator=False):
    """Resolve exactly one role-scoped source secret without escalation."""
    if spectator:
        if side not in (1, 2):
            raise ValueError("read-only spectators require --side 1 or --side 2")
        raw = os.environ.get("BEES_NATIVE_REMOTE_SPECTATOR_SIDE_TOKENS")
        if not raw:
            raise ValueError("set BEES_NATIVE_REMOTE_SPECTATOR_SIDE_TOKENS for spectator access")
        tokens = json.loads(raw)
        token = tokens.get(str(side)) if isinstance(tokens, dict) else None
        if not isinstance(token, str) or not 24 <= len(token) <= 256:
            raise ValueError(f"no valid spectator credential for side {side}")
        return token
    own = os.environ.get("BEES_NATIVE_REMOTE_TOKEN")
    if own:
        return own
    if "BEES_NATIVE_REMOTE_SIDE_TOKENS" in os.environ:
        if side is None:
            raise ValueError("specify --side 1 or --side 2 for independent multiplayer")
        tokens = json.loads(os.environ["BEES_NATIVE_REMOTE_SIDE_TOKENS"])
        token = tokens.get(str(side))
        if not isinstance(token, str):
            raise ValueError(f"no remote authentication token configured for side {side}")
        return token
    token = os.environ.get("BEES_NATIVE_REMOTE_TOKEN")
    if not token:
        raise ValueError("set BEES_NATIVE_REMOTE_TOKEN or BEES_NATIVE_REMOTE_SIDE_TOKENS")
    return token


HELP = """Commands:
  status               Show the latest world and selected friendly squads
  objective            Show the authored mission and reinforcement status
  codex                Show genuinely unlocked campaign ship classes
  rewards              Show exact committed campaign awards and retirements
  ready                Acknowledge the current hosted campaign briefing
  surrender confirm    Concede the battle (irreversible after host commits)
  activate-retreat     Open your side's authored retreat exit
  rejoin               Reconnect to the SAME recovered native host session
  ships                List currently visible ships and their positions
  squads               List friendly and visible target squads
  obstacles            List visible map objects and mining asteroids
  weapons              Inspect live weapon status on selected ships
  overlay on|off       Request authoritative weapon range/contact detail
  select ID [ID...]    Select one or more friendly squad IDs
  select all           Select all controllable root squads (not child drones)
  select clear         Clear selection and requested weapon overlays
  number 0..9          Select original numbered squad (0 means squad 10)
  cycle next|previous  Cycle living source squads in authored roster order
  move X Y             Move selected squads to world point
  fire X Y             Manual point fire; fire stop clears target
  held begin X Y       Begin sustained fire with selected squad weapons
  held aim X Y         Aim this connection's active held-fire gesture
  held end             Release sustained fire
  cease on|off         Set cease fire or attack on sight
  chase on|off         Enable/disable selected squad chase preference
  match on|off         Match selected ship speeds
  lock on|off          Lock/unlock selected squad's current enemy
  shoot ID             Choose native shooting strategy ID 1-40
  attack ID            Attack visible enemy squad
  circle ID            Circle visible enemy squad
  right-swipe ID       Right-swipe visible enemy squad
  left-swipe ID        Left-swipe visible enemy squad
  in-and-out ID        In-and-out attack against visible enemy squad
  guard ID             Guard a visible friendly squad
  mine ID              Mine a visible mining asteroid
  heal ID              Heal from a visible friendly Beehive
  retreat ID           Retreat through a visible friendly WarpGate
  patrol X1 Y1 X2 Y2   Patrol the requested rectangle
  hold                 Stop selected squad's active chase
  special beacon|detonate|charge
  help                 Print this command list
  quit                 Disconnect
"""


# The host validates all of these against the same source squad command
# implementation. Keep GUI and terminal choices on one shared mapping.
ENEMY_MANEUVERS = {
    "attack": "Attack",
    "circle": "Circle",
    "right-swipe": "RightSwipe",
    "left-swipe": "LeftSwipe",
    "in-and-out": "InAndOut",
}


class RemoteConsole:
    def __init__(self, connection, *, silent=False, expected_side=None,
                 expected_session_id=None, spectator=False):
        self.socket = connection
        self.silent = silent
        self.expected_side = expected_side
        self.spectator = spectator
        self.surrender_requested_episode = None
        self.surrender_request_id = None
        self.weapon_overlay_enabled = False
        # One host_session_id belongs to the original authoritative World
        # across native checkpoint recovery. Rejoining an unrelated host
        # with the same tokens must never carry old player authority.
        self.expected_session_id = expected_session_id
        self.host_session_id = None
        self.authenticated_side = None
        self.notifications = SimpleQueue()
        self.lock = threading.Lock()
        self.latest = None
        self.selected = []
        self.selected_scope = None
        self.next_request = 1
        self.held_scope = None
        self.held_begin_request = None
        self.alive = True
        self.last_terminal_episode = None
        self.last_dialogue_cue = None
        self.last_campaign_receipt = None
        self.last_battle_receipt = None
        self.briefing_acknowledgments = {}
        self.seen_ship_classes = {}

    def receive(self):
        try:
            with self.socket.makefile("r", encoding="utf-8") as stream:
                for line in stream:
                    message = json.loads(line)
                    kind = message.get("kind")
                    if kind == "world_state":
                        if (self.host_session_id is None or
                                message.get("viewer_side") != self.authenticated_side):
                            raise ValueError("unauthenticated or misrouted native World state")
                        episode = (message.get("world_id"),
                                   message.get("episode_revision"))
                        with self.lock:
                            # Squad IDs can be reused when campaign Levels
                            # or physical episodes turn over. Never carry a
                            # previous world's command selection into the
                            # next authoritative roster.
                            if self.selected_scope != episode:
                                self.selected.clear()
                                self.held_scope = None
                                self.held_begin_request = None
                                self.weapon_overlay_enabled = False
                                self.seen_ship_classes.clear()
                                # A first world snapshot may arrive after
                                # the reliably queued campaign commit. Never
                                # discard that receipt on initial admission.
                                # A true later episode must clear prior
                                # career-result presentation instead.
                                if self.selected_scope is not None:
                                    self.last_campaign_receipt = None
                                # Reliable terminal replies can overtake a
                                # replaceable unsent world_state. Keep an
                                # already received final receipt for exactly
                                # the World it authenticates, even when
                                # this is the first snapshot of the socket.
                                receipt = self.last_battle_receipt
                                if receipt is not None and not (
                                    receipt["kind"] == "battle_result" and
                                    (receipt.get("world_id"), receipt.get("episode_revision")) == episode
                                ) and not (
                                    receipt["kind"] == "battle_interrupted" and
                                    self.selected_scope is None
                                ):
                                    self.last_battle_receipt = None
                                self.briefing_acknowledgments.clear()
                                self.selected_scope = episode
                            # Skirmish Codex knowledge is local presentation:
                            # retain only classes already revealed by host
                            # fog filtering in THIS World episode.
                            if message.get("campaign_codex_gate") is not True:
                                for entry in message.get("visible_ship_classes") or []:
                                    kind = entry.get("ship_type")
                                    name = entry.get("name")
                                    if (type(kind) is int and 0 <= kind < 24 and
                                            isinstance(name, str) and name):
                                        self.seen_ship_classes[kind] = name
                            self.latest = message
                        dialogue = message.get("campaign_dialogue")
                        cue = ((message.get("world_id"), message.get("episode_revision"),
                                dialogue["sequence"]) if dialogue else None)
                        if cue is not None and cue != self.last_dialogue_cue:
                            self.last_dialogue_cue = cue
                            lines = dialogue.get("lines", [])
                            if self.silent:
                                self.notifications.put("Campaign dialogue: L next line, K dismiss")
                            else:
                                for speaker, line_text in lines:
                                    print(f"{speaker}: {line_text}")
                                if message.get("briefing_requires_ack") is True:
                                    print("Enter 'ready' after reading to start the hosted campaign.")
                        outcome = message.get("terminal_outcome")
                        episode = (message.get("world_id"), message.get("episode_revision"))
                        if outcome is not None and episode != self.last_terminal_episode:
                            self.last_terminal_episode = episode
                            notice = (f"Battle finished: {outcome.get('label', 'Result')}"
                                      f" — {outcome.get('reason', 'No reason reported')}")
                            self.notifications.put(notice)
                            if not self.silent:
                                print(notice)
                    elif kind == "campaign_receipt":
                        with self.lock:
                            self.last_campaign_receipt = message
                        notice = self.campaign_receipt_summary(message)
                        self.notifications.put(notice)
                        if not self.silent:
                            print(notice)
                    elif kind in ("battle_result", "battle_interrupted"):
                        # Reliable post-finalization evidence, independent
                        # of the lossy render stream's last world_state.
                        if (message.get("protocol") != 1 or
                                message.get("viewer_side") not in (1, 2) or
                                (self.expected_side is not None and
                                 message["viewer_side"] != self.expected_side)):
                            raise ValueError("invalid native multiplayer terminal receipt")
                        with self.lock:
                            current = self.latest
                            if kind == "battle_result" and current is not None and (
                                message.get("world_id"), message.get("episode_revision")
                            ) != (current["world_id"], current["episode_revision"]):
                                raise ValueError("native terminal receipt belongs to another episode")
                            self.last_battle_receipt = message
                        notice = (f"Battle finished: {message['label']} — {message['reason']}"
                                  if kind == "battle_result" else
                                  f"Battle interrupted: {message['reason']}")
                        self.notifications.put(notice)
                        if not self.silent:
                            print(notice)
                    elif kind == "briefing_result":
                        with self.lock:
                            pending = next((
                                cue for cue, request_id in self.briefing_acknowledgments.items()
                                if request_id == message.get("request_id")
                            ), None)
                            if pending is not None and message.get("processed") is not True:
                                self.briefing_acknowledgments.pop(pending, None)
                        notice = f"Briefing: {message.get('feedback', 'No acknowledgement')}"
                        self.notifications.put(notice)
                        if not self.silent:
                            print(notice)
                    elif kind == "welcome":
                        side = message.get("side")
                        session = message.get("host_session_id")
                        if (message.get("protocol") != 1 or
                                side not in (1, 2) or
                                self.host_session_id is not None or
                                not isinstance(session, str) or len(session) != 32 or
                                any(ch not in "0123456789abcdef" for ch in session) or
                                (self.expected_side is not None and
                                 side != self.expected_side) or
                                message.get("spectator") is not self.spectator or
                                message.get("mode") != (
                                    "authenticated_spectator" if self.spectator
                                    else "authenticated_player_side"
                                ) or
                                (self.expected_session_id is not None and
                                 session != self.expected_session_id)):
                            raise ValueError(
                                "native multiplayer authority or recovered host session differs"
                            )
                        with self.lock:
                            self.host_session_id = session
                            self.authenticated_side = side
                        note = ("Rejoined recovered native host"
                                if message.get("recovered_host") is True
                                else "Connected to native host")
                        role = "spectator" if self.spectator else "player"
                        self.notifications.put(f"{note}; {role} side {side} ready")
                        if not self.silent:
                            print(f"{note}; {role} side {side} ready")
                    elif kind == "held_fire_released":
                        self.held_scope = None
                        self.held_begin_request = None
                        note = "Held fire released by native host"
                        self.notifications.put(note)
                        if not self.silent:
                            print(note)
                    elif kind == "input_result":
                        if message.get("request_id") == self.surrender_request_id:
                            # A rejected order was never committed to the
                            # authoritative Level, so permit a fresh request.
                            if message.get("processed") is not True:
                                self.surrender_requested_episode = None
                            self.surrender_request_id = None
                        if message.get("request_id") == self.held_begin_request:
                            self.held_begin_request = None
                            if message.get("processed") is not True:
                                # The host rejected Begin; do not keep a
                                # fictional local gesture that blocks retry.
                                self.held_scope = None
                        note = f"Order {message['request_id']}: {message['feedback']}"
                        self.notifications.put(note)
                        if not self.silent:
                            print(note)
                    else:
                        self.notifications.put(f"Host: {message}")
                        if not self.silent:
                            print(f"Host: {message}")
        except (OSError, ValueError) as error:
            self.notifications.put(f"Connection failed: {error}")
            if not self.silent:
                print(f"Connection failed: {error}")
        finally:
            self.alive = False
            try:
                self.socket.shutdown(socket.SHUT_RDWR)
            except OSError:
                pass
            self.notifications.put("Connection closed")

    def snapshot(self):
        with self.lock:
            return self.latest

    def campaign_receipt_snapshot(self):
        with self.lock:
            return self.last_campaign_receipt

    def battle_receipt_snapshot(self):
        with self.lock:
            return self.last_battle_receipt

    def acknowledge_briefing(self):
        """Release the real hosted Level after this viewer finishes its cue.

        The host verifies the authenticated side, exact World/episode and
        original cue sequence. This is not a gameplay input or local timer.
        A retry is possible if the host rejects an outdated request.
        """
        if self.spectator:
            raise ValueError("spectators cannot acknowledge player campaign briefings")
        state = self.snapshot()
        dialogue = state.get("campaign_dialogue") if state else None
        if not dialogue or state.get("briefing_requires_ack") is not True:
            return False
        cue = (state["world_id"], state["episode_revision"], dialogue["sequence"])
        with self.lock:
            if cue in self.briefing_acknowledgments:
                return False
            request_id = self.next_request
            self.next_request += 1
            # Reserve before transmission: the network receiver may
            # process the host's immediate rejection before sendall returns.
            self.briefing_acknowledgments[cue] = request_id
        packet = {
            "kind": "briefing_ready",
            "request_id": request_id,
            "world_id": cue[0],
            "episode_revision": cue[1],
            "sequence": cue[2],
        }
        try:
            self.socket.sendall(
                (json.dumps(packet, separators=(",", ":")) + "\n").encode("utf-8")
            )
        except OSError:
            with self.lock:
                if self.briefing_acknowledgments.get(cue) == request_id:
                    del self.briefing_acknowledgments[cue]
            raise
        return True

    @staticmethod
    def objective_summary(state):
        """Display the host's current objective; never infer it from ships."""
        objective = state.get("campaign_objective")
        if objective is None:
            return None
        kind = objective["kind"]
        if kind == "survival":
            description = (f"Defend ship {objective['protected_ship_id']}"
                           f" (side {objective['defending_side']})")
            if objective.get("titania_defense"):
                description = "Defend Titania | " + description
                duration = objective.get("duration_seconds")
                elapsed = objective.get("elapsed_seconds")
                if duration and elapsed is not None:
                    description += f" | A.M.I. {min(100, max(0, int(100*elapsed/duration)))}%"
            elif objective.get("pluto_evacuation"):
                description = "Protect the Pluto evacuation | " + description
        elif kind == "demolition":
            description = (f"Demolish map target {objective['target_map_object_id']}"
                           f" (attacking side {objective['attacking_side']})")
        elif kind == "escort":
            description = (f"Escort ship {objective['escorted_ship_id']}"
                           f" to the destination (side {objective['escorting_side']})")
        elif kind == "extraction":
            side = objective["exiting_side"]
            if objective.get("mine_and_withdraw"):
                description = f"Mine resources and withdraw side {side}"
            elif objective.get("titania_minesweeper"):
                description = f"Guide side {side} through Titania's extraction zone"
            elif objective.get("retreat_on_exit"):
                description = f"Retreat side {side} through the exit"
            else:
                description = f"Extract ships from side {side}"
        elif kind == "withdrawal_pending":
            side = objective["exiting_side"]
            description = (f"Open the retreat exit for side {side}"
                           if state["viewer_side"] == side else
                           f"Prevent side {side} from withdrawing")
        elif kind == "barge_rescue":
            remaining = objective.get("barges_remaining")
            description = ("Rescue surviving barges" if remaining is None
                           else f"Rescue barges | {remaining} of 3 remain")
        elif kind == "pluto_reinforcements":
            description = "Pluto reinforcements"
            if objective.get("scout_selected") is False:
                description += " | Select the Scout"
            elif objective.get("combat_released") is False:
                description += " | Await the source briefing"
            elif objective.get("combat_released") is True:
                description += " | Defend against the arriving force"
        elif kind == "pluto_anomaly":
            description = "Investigate the Pluto anomaly"
            if objective.get("honeybee_spotted") is True:
                description += " | Honeybee identified"
            if objective.get("gunship_arrived") is True:
                description += " | Gunship arrived"
            if objective.get("honeybee_destroyed") is True:
                description += " | Honeybee destroyed"
        elif kind == "pluto_pushback":
            description = "Push back the enemy fleet"
        elif kind == "campaign_elimination":
            side = objective.get("attacking_side")
            description = ("Eliminate the enemy fleet"
                           if side == state["viewer_side"] else
                           "Defend against the attacking fleet")
        elif kind == "target_ship":
            attacking_side = objective["attacking_side"]
            target = objective["target_ship_id"]
            if state["viewer_side"] == attacking_side:
                description = f"Destroy named enemy flagship #{target}"
            else:
                description = f"Protect named target ship #{target} from side {attacking_side}"
        else:
            return None
        remaining = objective.get("seconds_remaining")
        if remaining is not None:
            description += f" | {remaining:.1f}s remaining"
        waves = state.get("reinforcements") or {}
        if waves.get("pending", 0):
            description += f" | {waves['pending']} reinforcement waves pending"
            if waves.get("next_tick") is not None:
                description += f" (next tick {waves['next_tick']})"
        return description

    @staticmethod
    def campaign_receipt_summary(message):
        outcome = message["campaign_outcome"]
        saved = "victory" if outcome.get("mission_success",
                                      outcome["winner_side"] == outcome["player_side"]) else "defeat"
        ships = len(outcome.get("awarded_ships", []))
        squads = len(outcome.get("awarded_squads", []))
        minerals = message.get("banked_minerals")
        income = f", {minerals} minerals banked" if minerals is not None else ""
        details = []
        retired = outcome.get("retired_fleet_ids") or []
        if retired:
            details.append(f"{len(retired)} permanent fleet retirements")
        if outcome.get("evacuated_personnel") is not None:
            details.append(f"{outcome['evacuated_personnel']} personnel evacuated")
        extra = ("; " + ", ".join(details)) if details else ""
        return (f"Campaign saved: {outcome['mission_id']} ({saved}); "
                f"{ships} ships and {squads} squads awarded{income}{extra}")

    @staticmethod
    def campaign_reward_details(message, known_classes=None):
        """Format only immutable career effects from a committed receipt."""
        outcome = message["campaign_outcome"]
        names = known_classes or {}
        lines = [f"Mission: {outcome['mission_id']}"]
        lines.append("Success" if outcome.get(
            "mission_success", outcome["winner_side"] == outcome["player_side"]
        ) else "Defeat")
        awarded = outcome.get("awarded_ships", [])
        if awarded:
            lines.append("FleetShips awarded:")
            for ship in awarded:
                kind = ship["ship_type"]
                label = names.get(kind, f"Type {kind}")
                lines.append(
                    f"  FleetShip #{ship['fleet_ship_id']}: {label}, "
                    f"SavedSquad #{ship['saved_squad_id']}"
                )
        squads = outcome.get("awarded_squads", [])
        if squads:
            lines.append("Saved squads awarded:")
            for squad in squads:
                lines.append(
                    f"  SavedSquad #{squad['saved_squad_id']} "
                    f"(template #{squad['template_saved_squad_id']})"
                )
        retired = outcome.get("retired_fleet_ids", [])
        if retired:
            lines.append("Permanently retired FleetShip IDs: " +
                         ", ".join(str(value) for value in retired))
        if outcome.get("evacuated_personnel") is not None:
            lines.append(f"Personnel evacuated: {outcome['evacuated_personnel']}")
        if message.get("banked_minerals") is not None:
            lines.append(f"Banked minerals: {message['banked_minerals']}")
        if message.get("campaign_score_delta") is not None:
            lines.append(f"Score delta: {message['campaign_score_delta']:+d}")
        return "\n".join(lines)

    @staticmethod
    def match_score_summary(state, final=None):
        """Use the immutable committed receipt whenever it is available."""
        evidence = (final if final is not None and
                    final.get("kind") == "battle_result" else state)
        parts = []
        if evidence.get("player_score") is not None:
            parts.append(f"score {evidence['player_score']}")
        counts = evidence.get("player_match_counts") or {}
        for field, label in (("ships_destroyed_by_player", "kills"),
                             ("ships_lost", "ships lost"),
                             ("ships_returned", "ships returned")):
            if counts.get(field) is not None:
                parts.append(f"{label} {counts[field]}")
        mined = evidence.get("player_minerals_mined")
        if mined is None:
            mined = counts.get("player_minerals_mined")
        if mined is not None:
            parts.append(f"minerals mined {mined}")
        return ", ".join(parts) if parts else None

    @staticmethod
    def source_number_owner(state, key):
        """Resolve a 1..0 source squad without renumbering casualties."""
        if key not in range(10):
            return None
        original = next((
            row.get("original_squad_count") for row in state["squads"]
            if row["controllable"] and row.get("source_number") is not None
            and row.get("original_squad_count")
        ), None)
        if not original:
            return None
        wanted = ((10 if key == 0 else key)-1) % original + 1
        return next((
            row["owner"] for row in state["squads"] if row["controllable"]
            and row.get("source_number") == wanted
        ), None)

    @staticmethod
    def cycle_source_owner(state, current, forward):
        """Match the playable native source registration order and eligibility."""
        eligible = [
            row["owner"] for row in state["squads"]
            if row["controllable"] and (
                row.get("source_number") is not None or
                (row.get("original_squad_count") is None and
                 row.get("source_root") is True))
        ]
        if not eligible:
            return None
        index = eligible.index(current) if current in eligible else None
        if index is None:
            return eligible[0 if forward else -1]
        return eligible[(index + (1 if forward else -1)) % len(eligible)]

    def codex_entries(self, state):
        if self.spectator or state is None:
            return None
        if state.get("campaign_codex_gate") is True:
            return state.get("campaign_codex_entries")
        if state.get("visible_ship_classes") is None:
            return None
        with self.lock:
            return [{"ship_type": kind, "name": name} for kind, name
                    in sorted(self.seen_ship_classes.items())]

    def selected_owners(self, state):
        squads = {row["owner"]["squad_id"]: row["owner"]
                  for row in state["squads"] if row["controllable"]
        scope = (state["world_id"], state["episode_revision"])
        with self.lock:
            if self.selected_scope != scope:
                return []
            return [squads[id] for id in self.selected if id in squads]

    def locate_squad(self, state, value, *, owned=None, friendly=None):
        squad_id = int(value)
        matches = [row for row in state["squads"]
                   if row["owner"]["squad_id"] == squad_id and
                   (owned is None or row["owned"] == owned) and
                   (owned is not True or row["controllable"]) and
                   (friendly is None or
                    (row["side"] == state["viewer_side"]) == friendly)]
        if len(matches) != 1:
            relation = ("controllable" if owned is True else
                        "friendly" if friendly is True else
                        "enemy" if friendly is False else "available")
            raise ValueError(f"squad {squad_id} is not an available {relation} target")
        return matches[0]["owner"]

    def send(self, action, state, owners):
        if self.spectator:
            raise ValueError("spectator seats cannot submit gameplay commands")
        # Aim and End belong to the authenticated peer's existing gesture.
        # Only Begin carries selected squad lifetimes; the native host
        # authorizes the gesture and owns the live weapon state.
        is_held = action[0] == "held_fire"
        is_side_action = action[0] in ("surrender", "activate_retreat")
        if not owners and not is_side_action and (
                not is_held or "Begin" in action[1]["action"]):
            raise ValueError("select at least one live friendly squad first")
        packet = {
            "kind": "input", "request_id": self.next_request,
            "action": {
                "kind": action[0], "input": {
                    "world_id": state["world_id"],
                    "episode_revision": state["episode_revision"],
                    **({} if is_held or is_side_action else {"squads": owners}),
                    **action[1],
                },
            },
        }
        # Physical input coordinates must be finite; Python's default
        # encoder otherwise emits NaN/Infinity outside valid JSON.
        encoded = (json.dumps(packet, separators=(",", ":"), allow_nan=False) + "\n").encode("utf-8")
        self.socket.sendall(encoded)
        self.next_request += 1
        return packet["request_id"]

    def request_weapon_overlay(self, state, owners):
        """Ask the host to project exact physical range and contact data.

        Interest is presentation only; host validates every squad lifetime
        and retains no alternative combat state.
        """
        if self.spectator:
            raise ValueError("spectators cannot request private weapon overlays")
        packet = {
            "kind": "weapon_overlay", "request_id": self.next_request,
            "world_id": state["world_id"],
            "episode_revision": state["episode_revision"],
            "squads": owners,
        }
        encoded = (json.dumps(packet, separators=(",", ":")) + "\n").encode("utf-8")
        self.socket.sendall(encoded)
        self.next_request += 1

    def surrender(self):
        if self.spectator:
            raise ValueError("spectators cannot surrender another player's battle")
        state = self.snapshot()
        if (state is None or state.get("can_surrender") is not True or
                state.get("terminal_outcome") is not None or
                state.get("briefing_paused") is True or
                self.battle_receipt_snapshot() is not None or
                self.held_scope is not None):
            raise ValueError("surrender unavailable during briefing, held fire, or after battle")
        scope = (state["world_id"], state["episode_revision"])
        if self.surrender_requested_episode == scope:
            raise ValueError("surrender already submitted for this battle")
        # Reserve the request before sending: the receiver can process a
        # fast host rejection before sendall returns on the sending thread.
        request_id = self.next_request
        self.surrender_requested_episode = scope
        self.surrender_request_id = request_id
        try:
            self.send(("surrender", {"side": state["viewer_side"]}), state, [])
        except (OSError, ValueError):
            if self.surrender_request_id == request_id:
                self.surrender_request_id = None
                self.surrender_requested_episode = None
            raise

    def held_fire(self, kind, target=None, *, owners=None):
        """Submit native Begin/Aim/End, retaining the original episode."""
        if self.spectator:
            raise ValueError("spectator seats cannot control weapons")
        state = self.snapshot()
        if state is None:
            raise ValueError("no authoritative world snapshot received yet")
        scope = (state["world_id"], state["episode_revision"])
        if self.held_scope is not None and (
                self.held_scope["world_id"], self.held_scope["episode_revision"]) != scope:
            self.held_scope = None
            self.held_begin_request = None
        if kind in ("begin", "aim") and (
                not isinstance(target, dict) or
                not all(isinstance(target.get(axis), (int, float)) and
                        math.isfinite(target[axis]) for axis in ("x", "y"))):
            raise ValueError("held fire needs a finite world aim point")
        if kind == "begin":
            if self.held_scope is not None:
                raise ValueError("end the existing held-fire gesture first")
            owners = self.selected_owners(state) if owners is None else owners
            if not owners or target is None:
                raise ValueError("held fire needs selected squads and an aim point")
            self.held_scope = {"world_id": scope[0], "episode_revision": scope[1]}
            self.held_begin_request = self.next_request
            try:
                self.send(("held_fire", {"action": {
                    "Begin": {"squads": owners, "target": target},
                }}), state, owners)
            except (OSError, ValueError):
                self.held_scope = None
                self.held_begin_request = None
                raise
        elif kind in ("aim", "end"):
            if self.held_scope is None:
                raise ValueError("no held-fire gesture is active")
            action = {"Aim": {"target": target}} if kind == "aim" else "End"
            self.send(("held_fire", {"action": action}), self.held_scope, [])
            if kind == "end":
                self.held_scope = None
                self.held_begin_request = None
        else:
            raise ValueError("unknown held-fire action")

    @staticmethod
    def flag(value):
        if value.lower() in ("on", "true", "yes"):
            return True
        if value.lower() in ("off", "false", "no"):
            return False
        raise ValueError("mode must be on or off")

    def command(self, text):
        args = shlex.split(text)
        if not args:
            return
        cmd, *argv = args
        cmd = cmd.lower()
        if cmd == "help":
            if self.spectator:
                print("Read-only spectator commands: status, objective, ships, "
                      "squads, obstacles, rejoin, help, quit")
            else:
                print(HELP)
            return
        if cmd == "quit":
            if self.held_scope is not None:
                try:
                    self.held_fire("end")
                except (OSError, ValueError):
                    pass  # Socket disconnect also releases host-owned weapons.
            self.alive = False
            return
        if self.spectator and cmd not in (
            "status", "objective", "ships", "squads", "obstacles", "codex"
        ):
            raise ValueError("spectator seats are read-only")
        if cmd == "rewards":
            if argv:
                raise ValueError("rewards takes no arguments")
            receipt = self.campaign_receipt_snapshot()
            if receipt is None:
                print("No committed campaign rewards received")
                return
            state = self.snapshot()
            classes = {entry["ship_type"]: entry["name"]
                       for entry in self.codex_entries(state) or []}
            print(self.campaign_reward_details(receipt, classes))
            return
        state = self.snapshot()
        if state is None:
            final = self.battle_receipt_snapshot()
            if cmd == "status" and final is not None:
                print(f"{final.get('label', 'Battle result')}: {final['reason']}")
            else:
                print("No authoritative world snapshot received yet")
            return
        if cmd == "status":
            print(f"World {state['world_id']} episode {state['episode_revision']}"
                  f" tick {state['tick']} side {state['viewer_side']}"
                  f" ships visible {len(state['ships'])}"
                  f" squads selected {self.selected}")
            final = self.battle_receipt_snapshot()
            outcome = state.get("terminal_outcome")
            if final is not None:
                if final["kind"] == "battle_result":
                    print(f"Battle finished: {final['label']} — {final['reason']}")
                else:
                    print(f"Battle interrupted: {final['reason']}")
            elif outcome is not None:
                print(f"Battle finished (awaiting durable receipt): {outcome['label']} — {outcome['reason']}")
            objective = self.objective_summary(state)
            if objective is not None:
                print(f"Objective: {objective}")
            score = self.match_score_summary(state, final)
            if score is not None:
                print(f"Match: {score}")
            if state.get("can_activate_retreat") is True:
                print("Retreat exit available: enter 'activate-retreat' to open it")
            receipt = self.campaign_receipt_snapshot()
            if receipt is not None:
                print(self.campaign_receipt_summary(receipt))
                delta = receipt.get("campaign_score_delta")
                if delta is not None:
                    print(f"Committed campaign score delta: {delta:+d}")
            return
        if cmd == "activate-retreat":
            if argv:
                raise ValueError("activate-retreat takes no arguments")
            if (state.get("can_activate_retreat") is not True or
                    state.get("terminal_outcome") is not None or
                    state.get("briefing_paused") is True or
                    self.battle_receipt_snapshot() is not None):
                raise ValueError("authoritative retreat activation is currently unavailable")
            self.send(("activate_retreat", {"side": state["viewer_side"]}), state, [])
            print("Retreat exit activation submitted to the native host")
            return
        if cmd == "surrender":
            if argv != ["confirm"]:
                raise ValueError("surrender is irreversible; enter 'surrender confirm' to concede")
            self.surrender()
            print("Surrender submitted; awaiting the host's durable battle result")
            return
        if cmd == "ready":
            print("Briefing acknowledgment sent" if self.acknowledge_briefing()
                  else "No unacknowledged hosted campaign briefing")
            return
        if cmd == "objective":
            objective = self.objective_summary(state)
            print(f"Objective: {objective}" if objective is not None
                  else "No active authored objective")
            return
        if cmd == "codex":
            if argv:
                raise ValueError("codex takes no arguments")
            entries = self.codex_entries(state)
            if entries is None:
                print("Fleet Codex unavailable to this player side or native host")
                return
            label = ("unlocked" if state.get("campaign_codex_gate") is True
                     else "encountered")
            print(f"Fleet Codex — {len(entries)} {label} ship classes")
            for entry in entries:
                print(f"  {entry['ship_type']:>2}  {entry['name']}")
            return
        if cmd == "ships":
            known = {entry["ship_type"]: entry["name"]
                     for entry in self.codex_entries(state) or []}
            for ship in state["ships"]:
                label = known.get(ship["ship_type"], f"type {ship['ship_type']}")
                print(f"  {ship['id']:>8} side {ship['side']} {label}"
                      f"  ({ship['x']:8.1f}, {ship['y']:8.1f})"
                      f"  HP {ship['health']}/{ship['max_health']}"
                      f" {'ours' if ship['owned'] else 'ally' if ship['friendly'] else 'enemy'}")
            return
        if cmd == "squads":
            known = {entry["ship_type"]: entry["name"]
                     for entry in self.codex_entries(state) or []}
            for squad in state["squads"]:
                owner = squad["owner"]
                lead = squad.get("lead_ship_type")
                lead_label = known.get(lead, f"type {lead}" if lead is not None else "")
                print(f"  {owner['squad_id']:>8} side {squad['side']}"
                      f" {'ours' if squad['owned'] else 'ally' if squad['side'] == state['viewer_side'] else 'enemy'}"
                      f"{' (orders disabled)' if squad['owned'] and not squad['controllable'] else ''}"
                      f" ships {squad['members']}"
                      f" {lead_label}"
                      f"{' hotkey '+str(squad['source_number']) if squad.get('source_number') is not None else ''}")
            return
        if cmd == "weapons":
            selected = {owner["squad_id"] for owner in self.selected_owners(state)}
            ship_ids = {
                ship_id for squad in state["squads"]
                if squad["owner"]["squad_id"] in selected
                for ship_id in squad["members"]
            }
            if not ship_ids:
                print("No live selected friendly ships")
            for ship in state["ships"]:
                if ship["id"] not in ship_ids:
                    continue
                print(f"  Ship {ship['id']} type {ship['ship_type']}")
                for weapon in ship.get("weapons", []):
                    charge = weapon.get("charge")
                    detail = f" charge {charge}" if charge is not None else ""
                    range_data = weapon.get("range")
                    if range_data is not None:
                        detail += f" range {range_data[2]:.1f} enabled={range_data[3]}"
                    detail += f" contacts {len(weapon.get('contacts', []))}"
                    print(f"    Slot {weapon['slot']}{detail}")
                if ship.get("striker_bomb_ready") is not None:
                    print(f"    Striker bomb ready: {ship['striker_bomb_ready']}")
                if ship.get("barge_charge") is not None:
                    print(f"    Barge charge: {ship['barge_charge']}")
            return
        if cmd == "obstacles":
            for object in state.get("obstacles", []):
                print(f"  {object['id']:>8} kind {object['kind']}"
                      f" ({object['x']:8.1f}, {object['y']:8.1f})"
                      f" {'mining' if object['mining'] else ''}"
                      f" HP {object['health']}")
            return
        if cmd in ("number", "cycle"):
            if cmd == "number" and len(argv) == 1 and argv[0] in "0123456789":
                owner = self.source_number_owner(state, int(argv[0]))
            elif cmd == "cycle" and argv in (["next"], ["previous"]):
                current = self.selected_owners(state)
                owner = self.cycle_source_owner(
                    state, current[0] if current else None, argv == ["next"])
            else:
                raise ValueError("use number 0..9 or cycle next|previous")
            if owner is None:
                print("No live original squad matches this shortcut")
                return
            with self.lock:
                self.selected = [owner["squad_id"]]
                self.selected_scope = (state["world_id"], state["episode_revision"])
            if self.weapon_overlay_enabled:
                self.request_weapon_overlay(state, [owner])
            print(f"Selected: {self.selected}")
            return
        if cmd == "select":
            if argv == ["all"]:
                # Match the native Ctrl+A source semantics: children retain
                # individual command capability but mass selection excludes
                # them to avoid doubling parent and drone orders.
                owners = [row["owner"] for row in state["squads"]
                          if row["controllable"] and row.get("source_root") is True]
            elif argv == ["clear"]:
                owners = []
            elif argv:
                owners = [self.locate_squad(state, value, owned=True) for value in argv]
            else:
                raise ValueError("select requires squad IDs, all, or clear")
            with self.lock:
                self.selected = [owner["squad_id"] for owner in owners]
                self.selected_scope = (state["world_id"], state["episode_revision"])
            if self.weapon_overlay_enabled:
                self.request_weapon_overlay(state, owners)
                if not owners:
                    self.weapon_overlay_enabled = False
            print(f"Selected: {self.selected}")
            return
        if cmd == "overlay" and len(argv) == 1:
            enabled = self.flag(argv[0])
            owners = self.selected_owners(state) if enabled else []
            if enabled and not owners:
                raise ValueError("select live friendly squads before enabling weapon overlay")
            self.request_weapon_overlay(state, owners)
            self.weapon_overlay_enabled = enabled
            print("Weapon contact overlay enabled" if enabled else "Weapon contact overlay disabled")
            return
        owners = self.selected_owners(state)
        if cmd in ("move", "fire") and len(argv) == 2:
            target = {"x": float(argv[0]), "y": float(argv[1])}
            if cmd == "move":
                self.send(("move", {"destination": target}), state, owners)
            else:
                self.send(("fire", {"target": target}), state, owners)
        elif cmd == "fire" and argv == ["stop"]:
            self.send(("fire", {"target": None}), state, owners)
        elif cmd == "held" and len(argv) == 3 and argv[0] in ("begin", "aim"):
            self.held_fire(argv[0], {"x": float(argv[1]), "y": float(argv[2])})
        elif cmd == "held" and argv == ["end"]:
            self.held_fire("end")
        elif cmd in ("cease", "chase", "match") and len(argv) == 1:
            kind, field = {
                "cease": ("cease_fire", "cease_fire"),
                "chase": ("chase", "chasing"),
                "match": ("match_speed", "match_speed"),
            }[cmd]
            self.send((kind, {field: self.flag(argv[0])}), state, owners)
        elif cmd == "lock" and len(argv) == 1:
            self.send(("lock_on", {"locked": self.flag(argv[0])}), state, owners)
        elif cmd == "shoot" and len(argv) == 1:
            strategy_id = int(argv[0])
            if strategy_id not in range(1, 41):
                raise ValueError("shooting strategy must be 1-40")
            self.send(("shooting", {"strategy_id": strategy_id}), state, owners)
        elif cmd in ENEMY_MANEUVERS and len(argv) == 1:
            target = self.locate_squad(state, argv[0], friendly=False)
            self.send(("support", {"action": {
                ENEMY_MANEUVERS[cmd]: {"target": target},
            }}), state, owners)
        elif cmd == "guard" and len(argv) == 1:
            target = self.locate_squad(state, argv[0], friendly=True)
            self.send(("support", {"action": {"Guard": {"target": target}}}),
                      state, owners)
        elif cmd == "mine" and len(argv) == 1:
            object_id = int(argv[0])
            if not any(obj["id"] == object_id and obj["mining"]
                       for obj in state.get("obstacles", [])):
                raise ValueError("mining asteroid is not currently visible")
            self.send(("support", {"action": {"Mining": {"asteroid_id": object_id}}}),
                      state, owners)
        elif cmd in ("heal", "retreat") and len(argv) == 1:
            ship_id = int(argv[0])
            if not any(ship["id"] == ship_id and ship["friendly"]
                       for ship in state["ships"]):
                raise ValueError("friendly heal/retreat target is not currently visible")
            action = ({"Heal": {"beehive_ids": [ship_id]}} if cmd == "heal"
                      else {"FullRetreat": {"warp_gate_id": ship_id}})
            self.send(("support", {"action": action}), state, owners)
        elif cmd == "patrol" and len(argv) == 4:
            x1, y1, x2, y2 = map(float, argv)
            self.send(("support", {"action": {
                "Patrol": {
                    "top_left": {"x": x1, "y": y1},
                    "bottom_right": {"x": x2, "y": y2},
                },
            }}), state, owners)
        elif cmd == "hold" and not argv:
            self.send(("support", {"action": "Hold"}), state, owners)
        elif cmd == "special" and len(argv) == 1 and argv[0] in ("beacon", "detonate", "charge"):
            kind = {
                "beacon": "DropBeacon", "detonate": "Detonate", "charge": "Charge",
            }[argv[0]]
            self.send(("special", {"kind": kind}), state, owners)
        else:
            raise ValueError("unknown command or incorrect arguments; enter help")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("host", help="native host IP or DNS name")
    parser.add_argument("port", type=int)
    parser.add_argument("--side", type=int, choices=(1, 2),
                        help="authenticated faction for commanders or spectators")
    parser.add_argument("--spectator", action="store_true",
                        help="join with a separate read-only faction spectator credential")
    args = parser.parse_args()
    try:
        token = remote_token(args.side, spectator=args.spectator)
    except (ValueError, json.JSONDecodeError) as error:
        parser.error(str(error))
    def connect(expected_session_id=None):
        connection = socket.create_connection((args.host, args.port), timeout=10)
        connection.settimeout(None)
        try:
            console = RemoteConsole(
                connection, expected_side=args.side,
                expected_session_id=expected_session_id,
                spectator=args.spectator,
            )
            connection.sendall(
                (json.dumps({"kind": "hello", "token": token,
                             "spectator": args.spectator,
                             "resume_session_id": expected_session_id}) + "\n").encode("utf-8")
            )
            receiver = threading.Thread(target=console.receive, daemon=True)
            receiver.start()
            return connection, console, receiver
        except BaseException:
            connection.close()
            raise

    connection, console, receiver = connect()
    if args.spectator:
        print("Read-only spectator commands: status, objective, ships, squads, "
              "obstacles, rejoin, help, quit")
    else:
        print(HELP)
    try:
        while True:
            try:
                line = input("bees> ")
                if line.strip().lower() == "rejoin":
                    if console.alive:
                        print("The native host connection is already active")
                        continue
                    session = console.host_session_id or console.expected_session_id
                    if session is None:
                        print("No authenticated host session to rejoin")
                        continue
                    try:
                        new_connection, new_console, new_receiver = connect(session)
                    except OSError as error:
                        print(f"Recovered native host unavailable: {error}")
                        continue
                    # The next socket belongs to a different peer ID.
                    # Discard all old episode/squad/held-fire state.
                    connection.close()
                    receiver.join(timeout=1)
                    connection, console, receiver = (
                        new_connection, new_console, new_receiver
                    )
                    continue
                if not console.alive:
                    if line.strip().lower() == "quit":
                        break
                    # A final receipt is useful even when the host closes
                    # before the next input prompt. Preserve read-only
                    # access to already received, side-authenticated data.
                    readonly = {"status", "objective", "ships", "squads",
                                "obstacles", "weapons", "codex", "rewards", "help"}
                    keyword = line.strip().split(None, 1)
                    if keyword and keyword[0].lower() in readonly:
                        console.command(line)
                    else:
                        print("Connection closed; use 'rewards', 'status', 'rejoin', or 'quit'")
                    continue
                console.command(line)
                if line.strip().lower() == "quit":
                    break
            except (EOFError, KeyboardInterrupt):
                break
            except (OSError, ValueError) as error:
                print(f"Command error: {error}")
    finally:
        try:
            connection.shutdown(socket.SHUT_RDWR)
        except OSError:
            pass
        connection.close()
        receiver.join(timeout=1)


if __name__ == "__main__":
    main()
