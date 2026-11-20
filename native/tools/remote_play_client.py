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


def remote_token(side):
    """Resolve the caller's identity; a player needs only their own secret."""
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
  ready                Acknowledge the current hosted campaign briefing
  ships                List currently visible ships and their positions
  squads               List friendly and visible target squads
  obstacles            List visible map objects and mining asteroids
  select ID [ID...]    Select one or more friendly squad IDs
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
    def __init__(self, connection, *, silent=False, expected_side=None):
        self.socket = connection
        self.silent = silent
        self.expected_side = expected_side
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

    def receive(self):
        try:
            with self.socket.makefile("r", encoding="utf-8") as stream:
                for line in stream:
                    message = json.loads(line)
                    kind = message.get("kind")
                    if kind == "world_state":
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
                        if self.expected_side is not None and side != self.expected_side:
                            self.notifications.put(f"Authenticated as side {side}, expected {self.expected_side}; disconnecting")
                            try:
                                self.socket.shutdown(socket.SHUT_RDWR)
                            except OSError:
                                pass
                            break
                        self.notifications.put(f"Authenticated; player side {side} ready")
                        if not self.silent:
                            print(f"Authenticated; player side {side} ready")
                    elif kind == "held_fire_released":
                        self.held_scope = None
                        self.held_begin_request = None
                        note = "Held fire released by native host"
                        self.notifications.put(note)
                        if not self.silent:
                            print(note)
                    elif kind == "input_result":
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
        elif kind == "demolition":
            description = (f"Demolish map target {objective['target_map_object_id']}"
                           f" (attacking side {objective['attacking_side']})")
        elif kind == "escort":
            description = (f"Escort ship {objective['escorted_ship_id']}"
                           f" to the destination (side {objective['escorting_side']})")
        elif kind == "extraction":
            description = f"Extract ships from side {objective['exiting_side']}"
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
        return (f"Campaign saved: {outcome['mission_id']} ({saved}); "
                f"{ships} ships and {squads} squads awarded{income}")

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
        # Aim and End belong to the authenticated peer's existing gesture.
        # Only Begin carries selected squad lifetimes; the native host
        # authorizes the gesture and owns the live weapon state.
        is_held = action[0] == "held_fire"
        if not owners and (not is_held or "Begin" in action[1]["action"]):
            raise ValueError("select at least one live friendly squad first")
        packet = {
            "kind": "input", "request_id": self.next_request,
            "action": {
                "kind": action[0], "input": {
                    "world_id": state["world_id"],
                    "episode_revision": state["episode_revision"],
                    **({} if is_held else {"squads": owners}),
                    **action[1],
                },
            },
        }
        self.socket.sendall((json.dumps(packet, separators=(",", ":")) + "\n").encode("utf-8"))
        self.next_request += 1

    def held_fire(self, kind, target=None, *, owners=None):
        """Submit native Begin/Aim/End, retaining the original episode."""
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
        state = self.snapshot()
        if state is None:
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
            receipt = self.campaign_receipt_snapshot()
            if receipt is not None:
                print(self.campaign_receipt_summary(receipt))
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
        if cmd == "ships":
            for ship in state["ships"]:
                print(f"  {ship['id']:>8} side {ship['side']} type {ship['ship_type']:>2}"
                      f"  ({ship['x']:8.1f}, {ship['y']:8.1f})"
                      f"  HP {ship['health']}/{ship['max_health']}"
                      f" {'ours' if ship['owned'] else 'ally' if ship['friendly'] else 'enemy'}")
            return
        if cmd == "squads":
            for squad in state["squads"]:
                owner = squad["owner"]
                print(f"  {owner['squad_id']:>8} side {squad['side']}"
                      f" {'ours' if squad['owned'] else 'ally' if squad['side'] == state['viewer_side'] else 'enemy'}"
                      f"{' (orders disabled)' if squad['owned'] and not squad['controllable'] else ''}"
                      f" ships {squad['members']}")
            return
        if cmd == "obstacles":
            for object in state.get("obstacles", []):
                print(f"  {object['id']:>8} kind {object['kind']}"
                      f" ({object['x']:8.1f}, {object['y']:8.1f})"
                      f" {'mining' if object['mining'] else ''}"
                      f" HP {object['health']}")
            return
        if cmd == "select":
            if not argv:
                raise ValueError("select requires at least one squad ID")
            owners = [self.locate_squad(state, value, owned=True) for value in argv]
            with self.lock:
                self.selected = [owner["squad_id"] for owner in owners]
                self.selected_scope = (state["world_id"], state["episode_revision"])
            print(f"Selected: {self.selected}")
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
                        help="authenticated side for independent multiplayer")
    args = parser.parse_args()
    try:
        token = remote_token(args.side)
    except (ValueError, json.JSONDecodeError) as error:
        parser.error(str(error))
    with socket.create_connection((args.host, args.port), timeout=10) as connection:
        connection.settimeout(None)
        console = RemoteConsole(connection, expected_side=args.side)
        connection.sendall((json.dumps({"kind": "hello", "token": token}) +
                            "\n").encode("utf-8"))
        receiver = threading.Thread(target=console.receive, daemon=True)
        receiver.start()
        print(HELP)
        while console.alive:
            try:
                line = input("bees> ")
                console.command(line)
            except (EOFError, KeyboardInterrupt):
                break
            except (OSError, ValueError) as error:
                print(f"Command error: {error}")
        try:
            connection.shutdown(socket.SHUT_RDWR)
        except OSError:
            pass
        receiver.join(timeout=1)


if __name__ == "__main__":
    main()
