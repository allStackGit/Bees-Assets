#!/usr/bin/env python3
"""Control an existing native Bees battle over its authenticated co-op socket.

This is a terminal client for the real native host, not a second simulator.
The local game and this client share one player side and command authority.
Requires BEES_NATIVE_REMOTE_TOKEN to match the server's environment.
"""
import argparse
import json
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
  ships                List currently visible ships and their positions
  squads               List friendly and visible target squads
  obstacles            List visible map objects and mining asteroids
  select ID [ID...]    Select one or more friendly squad IDs
  move X Y             Move selected squads to world point
  fire X Y             Manual point fire; fire stop clears target
  cease on|off         Set cease fire or attack on sight
  chase on|off         Enable/disable selected squad chase preference
  match on|off         Match selected ship speeds
  lock on|off          Lock/unlock selected squad's current enemy
  shoot ID             Choose native shooting strategy ID 1-40
  attack ID            Attack visible enemy squad
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


class RemoteConsole:
    def __init__(self, connection, *, silent=False, expected_side=None):
        self.socket = connection
        self.silent = silent
        self.expected_side = expected_side
        self.notifications = SimpleQueue()
        self.lock = threading.Lock()
        self.latest = None
        self.selected = []
        self.next_request = 1
        self.alive = True
        self.last_terminal_episode = None
        self.last_dialogue_cue = None

    def receive(self):
        try:
            with self.socket.makefile("r", encoding="utf-8") as stream:
                for line in stream:
                    message = json.loads(line)
                    kind = message.get("kind")
                    if kind == "world_state":
                        with self.lock:
                            self.latest = message
                        dialogue = message.get("campaign_dialogue")
                        cue = ((message.get("world_id"), message.get("episode_revision"),
                                dialogue["sequence"]) if dialogue else None)
                        if cue is not None and cue != self.last_dialogue_cue:
                            self.last_dialogue_cue = cue
                            for speaker, line_text in dialogue["lines"]:
                                notice = f"{speaker}: {line_text}"
                                self.notifications.put(notice)
                                if not self.silent:
                                    print(notice)
                        outcome = message.get("terminal_outcome")
                        episode = (message.get("world_id"), message.get("episode_revision"))
                        if outcome is not None and episode != self.last_terminal_episode:
                            self.last_terminal_episode = episode
                            notice = (f"Battle finished: {outcome.get('label', 'Result')}"
                                      f" — {outcome.get('reason', 'No reason reported')}")
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
                    elif kind == "input_result":
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

    def selected_owners(self, state):
        squads = {row["owner"]["squad_id"]: row["owner"]
                  for row in state["squads"] if row["owned"]}
        return [squads[id] for id in self.selected if id in squads]

    def locate_squad(self, state, value, *, owned):
        squad_id = int(value)
        matches = [row for row in state["squads"]
                   if row["owner"]["squad_id"] == squad_id and
                   row["owned"] == owned]
        if len(matches) != 1:
            raise ValueError(f"squad {squad_id} is not an available {'friendly' if owned else 'enemy'} target")
        return matches[0]["owner"]

    def send(self, action, state, owners):
        if not owners:
            raise ValueError("select at least one live friendly squad first")
        packet = {
            "kind": "input", "request_id": self.next_request,
            "action": {
                "kind": action[0], "input": {
                    "world_id": state["world_id"],
                    "episode_revision": state["episode_revision"],
                    "squads": owners,
                    **action[1],
                },
            },
        }
        self.socket.sendall((json.dumps(packet, separators=(",", ":")) + "\n").encode("utf-8"))
        self.next_request += 1

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
            outcome = state.get("terminal_outcome")
            if outcome is not None:
                print(f"Battle finished: {outcome['label']} — {outcome['reason']}")
            return
        if cmd == "ships":
            for ship in state["ships"]:
                print(f"  {ship['id']:>8} side {ship['side']} type {ship['ship_type']:>2}"
                      f"  ({ship['x']:8.1f}, {ship['y']:8.1f})"
                      f"  HP {ship['health']}/{ship['max_health']}"
                      f" {'ours' if ship['owned'] else ''}")
            return
        if cmd == "squads":
            for squad in state["squads"]:
                owner = squad["owner"]
                print(f"  {owner['squad_id']:>8} side {squad['side']}"
                      f" {'ours' if squad['owned'] else 'target'}"
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
            self.selected = [owner["squad_id"] for owner in owners]
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
        elif cmd == "attack" and len(argv) == 1:
            target = self.locate_squad(state, argv[0], owned=False)
            self.send(("support", {"action": {"Attack": {"target": target}}}),
                      state, owners)
        elif cmd == "guard" and len(argv) == 1:
            target = self.locate_squad(state, argv[0], owned=True)
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
            if not any(ship["id"] == ship_id and ship["owned"]
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
