#!/usr/bin/env python3
"""Graphical cooperative native Bees client (standard-library Tk).

This window renders authoritative remote snapshots, not a second game
simulation. Every action is an existing native typed player input. The
native host alone owns physics, combat, fog, squad commands and outcomes.
"""
import argparse
import json
import math
import os
import socket
import threading
import time
import tkinter as tk
from tkinter import ttk

from remote_play_client import ENEMY_MANEUVERS, RemoteConsole, remote_token


BACKGROUND = "#101925"
FRIENDLY = "#67b9ff"
ENEMY = "#f0a365"
NEUTRAL = "#8a96a8"
MINERAL = "#d9c778"
OUTLINE = "#cad7e6"
MODES = {
    "Move": "move", "Attack": "attack", "Circle": "circle",
    "Right swipe": "right-swipe", "Left swipe": "left-swipe",
    "In and out": "in-and-out", "Guard": "guard",
    "Mine": "mine", "Heal": "heal", "Retreat": "retreat",
    "Point fire": "fire", "Patrol": "patrol",
    "Held fire": "held-fire",
}


class RemoteBattleWindow:
    def __init__(self, root, console, connection):
        self.root = root
        self.console = console
        self.connection = connection
        self.root.title("Bees — native cooperative battle")
        self.root.geometry("1140x760")
        self.root.minsize(720, 450)
        self.root.configure(bg=BACKGROUND)

        self.frame = ttk.Frame(root)
        self.frame.pack(fill="both", expand=True)
        self.toolbar = ttk.Frame(self.frame, padding=6)
        self.toolbar.pack(fill="x")
        self.toolbar_main = ttk.Frame(self.toolbar)
        self.toolbar_main.pack(fill="x")
        self.toolbar_tactics = ttk.Frame(self.toolbar)
        self.toolbar_tactics.pack(fill="x", pady=(3, 0))
        self.toolbar_special = ttk.Frame(self.toolbar)
        self.toolbar_special.pack(fill="x", pady=(3, 0))
        self.battle_area = ttk.Frame(self.frame)
        self.battle_area.pack(fill="both", expand=True)
        self.canvas = tk.Canvas(self.battle_area, bg=BACKGROUND, highlightthickness=0)
        self.canvas.pack(side="left", fill="both", expand=True)
        self.sidebar = ttk.Frame(self.battle_area, padding=6, width=220)
        self.sidebar.pack(side="right", fill="y")
        self.sidebar.pack_propagate(False)
        ttk.Label(self.sidebar, text="Player squads").pack(anchor="w")
        self.squad_list = tk.Listbox(self.sidebar, selectmode=tk.EXTENDED,
                                     exportselection=False, height=15, width=27)
        self.squad_list.pack(fill="both", expand=True)
        self.sidebar_owners = []
        self.sidebar_updating = False
        self.squad_list.bind("<<ListboxSelect>>", self.sidebar_select)
        self.squad_detail = tk.StringVar(value="Select a squad")
        ttk.Label(self.sidebar, textvariable=self.squad_detail, wraplength=200,
                  justify="left").pack(anchor="w", pady=(8, 0))
        ttk.Label(self.sidebar, text="Shooting priority (1-40)").pack(
            anchor="w", pady=(10, 0))
        self.strategy = tk.StringVar(value="1")
        self.strategy_owner = None
        self.strategy_picker = ttk.Combobox(
            self.sidebar, textvariable=self.strategy, width=7, state="readonly",
            values=[str(i) for i in range(1, 41)],
        )
        self.strategy_picker.pack(anchor="w")
        ttk.Button(self.sidebar, text="Apply shooting priority",
                   command=self.apply_shooting).pack(anchor="w", pady=(4, 0))
        self.footer = ttk.Frame(self.frame, padding=6)
        self.footer.pack(fill="x")
        self.status = tk.StringVar(value="Connecting to native host...")
        ttk.Label(self.footer, textvariable=self.status).pack(side="left")
        self.info = tk.StringVar(value="Left-click select, Shift+click add, right-click context order")
        ttk.Label(self.footer, textvariable=self.info).pack(side="right")

        self.mode = None
        self.patrol_start = None
        self.held_drag = False
        self.last_held_aim_at = 0.0
        self.state = None
        self.drawn_scope = None
        self.shown_terminal_receipt = None
        self.map_bounds = None
        self.camera = None
        self.zoom = 1.0
        self.offset = (0.0, 0.0)
        self.drag_origin = None
        self.drag_box = None
        self.mouse_middle = None
        self.selected = []
        self.last_feedback = ""
        self.dialogue_identity = None
        self.dialogue_line = 0
        self.view_width = 1
        self.view_height = 1

        for title in ("Move", "Attack", "Circle", "Right swipe", "Left swipe",
                      "In and out", "Guard", "Mine", "Heal", "Retreat", "Point fire",
                      "Patrol", "Held fire"):
            group = self.toolbar_main if title in (
                "Move", "Attack", "Guard", "Mine", "Heal", "Retreat", "Point fire",
                "Patrol", "Held fire",
            ) else self.toolbar_tactics
            ttk.Button(group, text=title,
                       command=lambda t=title: self.set_mode(MODES[t])
                       ).pack(side="left", padx=2)
        for title, action in [
            ("Stop fire", lambda: self.send("fire", {"target": None})),
            ("Cease", lambda: self.send("cease_fire", {"cease_fire": True})),
            ("Fire at will", lambda: self.send("cease_fire", {"cease_fire": False})),
            ("Chase on", lambda: self.send("chase", {"chasing": True})),
            ("Chase off", lambda: self.send("chase", {"chasing": False})),
            ("Match speed", lambda: self.send("match_speed", {"match_speed": True})),
            ("Own speed", lambda: self.send("match_speed", {"match_speed": False})),
            ("Hold", lambda: self.send("support", {"action": "Hold"})),
            ("Lock on", lambda: self.send("lock_on", {"locked": True})),
            ("Unlock", lambda: self.send("lock_on", {"locked": False})),
            ("Beacon", lambda: self.send("special", {"kind": "DropBeacon"})),
            ("Detonate", lambda: self.send("special", {"kind": "Detonate"})),
            ("Charge", lambda: self.send("special", {"kind": "Charge"})),
            ("Fit", self.fit),
        ]:
            group = self.toolbar_special if title in (
                "Beacon", "Detonate", "Charge", "Fit"
            ) else self.toolbar_tactics
            ttk.Button(group, text=title, command=action
                       ).pack(side="left", padx=2)

        self.canvas.bind("<ButtonPress-1>", self.left_down)
        self.canvas.bind("<B1-Motion>", self.left_drag)
        self.canvas.bind("<ButtonRelease-1>", self.left_up)
        # Tk may deliver a gesture release to another widget after the
        # pointer leaves the map. Always close the host-owned held lease.
        self.root.bind("<ButtonRelease-1>", self.release_held_on_root, add="+")
        self.canvas.bind("<Button-3>", self.context_click)
        self.canvas.bind("<MouseWheel>", self.wheel)
        self.canvas.bind("<Button-4>", lambda e: self.zoom_at(e, 1.2))
        self.canvas.bind("<Button-5>", lambda e: self.zoom_at(e, 1 / 1.2))
        self.canvas.bind("<ButtonPress-2>", self.pan_start)
        self.canvas.bind("<B2-Motion>", self.pan_move)
        self.canvas.bind("<Configure>", self.refresh)
        self.root.bind("<Escape>", lambda e: self.set_mode(None))
        self.root.bind("<Control-a>", self.select_all)
        self.root.bind("<KeyPress-l>", self.advance_dialogue)
        self.root.bind("<KeyPress-k>", self.skip_dialogue)
        self.root.protocol("WM_DELETE_WINDOW", self.close)
        self.root.after(50, self.poll)

    def notice(self, message):
        self.last_feedback = message
        self.status.set(message)

    def set_mode(self, mode):
        if self.held_drag:
            self.finish_held_fire()
        self.mode = mode
        self.patrol_start = None
        if mode == "held-fire":
            self.notice("Drag on the map to hold fire; release the mouse to stop")
        else:
            self.notice(f"Select a {'map point' if mode in ('move', 'fire', 'patrol') else 'visible target'}"
                        f" for {mode}" if mode else "Order mode cancelled")

    def apply_shooting(self):
        self.send("shooting", {"strategy_id": int(self.strategy.get())})

    def fit(self):
        self.zoom = 1.0
        self.offset = (0.0, 0.0)
        self.draw()

    def transform(self):
        bounds = self.map_bounds
        if bounds is None:
            return 1.0, 0.0, 0.0
        (x0, y0), (x1, y1) = bounds
        width, height = max(1, self.canvas.winfo_width()), max(1, self.canvas.winfo_height())
        scale = 0.93 * min(width / max(1.0, x1 - x0), height / max(1.0, y1 - y0))
        scale *= self.zoom
        cx = width * 0.5 - (x0 + x1) * 0.5 * scale + self.offset[0]
        cy = height * 0.5 + (y0 + y1) * 0.5 * scale + self.offset[1]
        return scale, cx, cy

    def to_screen(self, x, y):
        scale, cx, cy = self.transform()
        return x * scale + cx, -y * scale + cy

    def to_world(self, x, y):
        scale, cx, cy = self.transform()
        return (x - cx) / scale, (cy - y) / scale

    def zoom_at(self, event, factor):
        before = self.to_world(event.x, event.y)
        self.zoom = min(32.0, max(0.25, self.zoom * factor))
        after = self.to_screen(*before)
        self.offset = (self.offset[0] + event.x - after[0],
                       self.offset[1] + event.y - after[1])
        self.draw()

    def wheel(self, event):
        self.zoom_at(event, 1.2 if event.delta > 0 else 1 / 1.2)

    def pan_start(self, event):
        self.mouse_middle = (event.x, event.y)

    def pan_move(self, event):
        if self.mouse_middle is None:
            return
        dx, dy = event.x - self.mouse_middle[0], event.y - self.mouse_middle[1]
        self.offset = (self.offset[0] + dx, self.offset[1] + dy)
        self.mouse_middle = (event.x, event.y)
        self.draw()

    def visible_dialogue(self):
        dialogue = self.state.get("campaign_dialogue") if self.state else None
        if dialogue is None:
            return None
        lines = dialogue.get("lines", [])
        return lines[self.dialogue_line] if self.dialogue_line < len(lines) else None

    def finish_briefing(self):
        # Only dedicated hosted campaigns require a reply. The source
        # host verifies this viewer's side and the exact cue/episode; no
        # local game clock or campaign progression is reproduced here.
        try:
            if self.console.acknowledge_briefing():
                self.notice("Campaign briefing acknowledgment sent; awaiting host and other viewers")
        except (OSError, ValueError) as error:
            self.notice(f"Campaign briefing acknowledgment failed: {error}")

    def advance_dialogue(self, _event=None):
        if self.visible_dialogue() is not None:
            self.dialogue_line += 1
            if self.visible_dialogue() is None:
                self.finish_briefing()
            self.draw()
        elif self.state and self.state.get("briefing_requires_ack"):
            # A rejected acknowledgment leaves the same source cue
            # visible until the host actually advances the Level.
            self.finish_briefing()

    def skip_dialogue(self, _event=None):
        dialogue = self.state.get("campaign_dialogue") if self.state else None
        if dialogue is not None:
            self.dialogue_line = len(dialogue.get("lines", []))
            self.finish_briefing()
            self.draw()

    def poll(self):
        while not self.console.notifications.empty():
            self.notice(self.console.notifications.get_nowait())
        state = self.console.snapshot()
        if state is not None:
            key = (state["world_id"], state["episode_revision"], state["tick"])
            if key != self.drawn_scope:
                if self.drawn_scope is not None and (
                    key[0] != self.drawn_scope[0] or
                    key[1] != self.drawn_scope[1]
                ):
                    self.selected = []
                    self.patrol_start = None
                    self.held_drag = False
                    self.mode = None
                self.state = state
                self.drawn_scope = key
                dialogue = state.get("campaign_dialogue")
                identity = ((key[0], key[1], dialogue["sequence"])
                            if dialogue is not None else None)
                if identity != self.dialogue_identity:
                    self.dialogue_identity = identity
                    self.dialogue_line = 0
                self.update_sidebar()
                bounds = state.get("map_bounds")
                if self.map_bounds != bounds:
                    self.map_bounds = bounds
                    self.fit()
                else:
                    self.draw()
                if not self.last_feedback:
                    self.status.set(f"World {key[0]}, episode {key[1]}, tick {key[2]}")
        terminal = self.console.battle_receipt_snapshot()
        if terminal != self.shown_terminal_receipt:
            self.shown_terminal_receipt = terminal
            self.draw()
        if self.console.alive:
            self.root.after(50, self.poll)
        else:
            campaign = self.console.campaign_receipt_snapshot()
            final = self.console.battle_receipt_snapshot()
            outcome = self.state.get("terminal_outcome") if self.state else None
            self.held_drag = False
            if final is not None:
                self.notice(
                    f"Battle finished: {final['label']} — {final['reason']}"
                    if final["kind"] == "battle_result"
                    else f"Battle interrupted: {final['reason']}"
                )
            elif outcome is not None:
                self.notice("Host closed before a reliable final battle receipt")
            else:
                self.notice("Disconnected from host; battle is no longer interactive")
            if campaign is not None:
                self.notice(self.console.campaign_receipt_summary(campaign))
            self.draw()

    def refresh(self, _event=None):
        self.draw()

    def squad_for_ship(self, ship_id, *, owned=None, friendly=None):
        if self.state is None:
            return None
        for squad in self.state["squads"]:
            if (ship_id in squad["members"] and
                    (owned is None or squad["owned"] == owned) and
                    (friendly is None or
                     (squad["side"] == self.state["viewer_side"]) == friendly)):
                return squad
        return None

    def obstacle_screen_shape(self, obj):
        """Project the authoritative physics collider, including local offsets."""
        collider = obj.get("collider") or {}
        kind = collider.get("kind")
        scale, cx, cy = self.transform()
        if kind in ("circle", "box", "polygon"):
            angle = math.radians(obj["rotation"])
            sn, cs = math.sin(angle), math.cos(angle)

            def project(px, py):
                wx = obj["x"] + px * cs - py * sn
                wy = obj["y"] + px * sn + py * cs
                return wx * scale + cx, -wy * scale + cy

            if kind == "circle":
                x, y = project(*collider["offset"])
                radius = collider["radius"] * scale
                return "oval", (x-radius, y-radius, x+radius, y+radius)
            if kind == "box":
                ox, oy = collider["offset"]
                hx, hy = collider["half_extents"]
                vertices = ((ox-hx, oy-hy), (ox+hx, oy-hy),
                            (ox+hx, oy+hy), (ox-hx, oy+hy))
            else:
                vertices = collider["vertices"]
            if len(vertices) >= 3:
                points = []
                for px, py in vertices:
                    points.extend(project(px, py))
                return "polygon", points

        # Large or unsupported hulls already carry the host's physics AABB.
        bounds = obj.get("collider_bounds")
        if bounds is not None:
            ax, ay = self.to_screen(*bounds[0])
            bx, by = self.to_screen(*bounds[1])
            return "bounds", (min(ax, bx), min(ay, by),
                              max(ax, bx), max(ay, by))
        return "point", self.to_screen(obj["x"], obj["y"])

    @staticmethod
    def obstacle_distance(shape, points, x, y):
        """Screen-space distance to a collider, zero when inside its hull."""
        if shape == "point":
            return math.hypot(x-points[0], y-points[1])
        if shape == "oval":
            ax, ay, bx, by = points
            return max(0.0, math.hypot(x-(ax+bx)/2, y-(ay+by)/2) - (bx-ax)/2)
        if shape == "bounds":
            ax, ay, bx, by = points
            vertices = ((ax, ay), (bx, ay), (bx, by), (ax, by))
        else:
            vertices = list(zip(points[::2], points[1::2]))
        inside, nearest = False, math.inf
        for i, (ax, ay) in enumerate(vertices):
            bx, by = vertices[(i+1) % len(vertices)]
            dx, dy = bx-ax, by-ay
            length_sq = dx*dx + dy*dy
            t = max(0.0, min(1.0, ((x-ax)*dx + (y-ay)*dy) / length_sq)) if length_sq else 0.0
            nearest = min(nearest, math.hypot(x-(ax+t*dx), y-(ay+t*dy)))
            if (ay > y) != (by > y) and x < (bx-ax)*(y-ay)/(by-ay) + ax:
                inside = not inside
        return 0.0 if inside else nearest

    def hit(self, x, y):
        if self.state is None:
            return None, None
        closest = None
        distance = 17 ** 2
        for ship in self.state["ships"]:
            sx, sy = self.to_screen(ship["x"], ship["y"])
            d = (sx - x) ** 2 + (sy - y) ** 2
            if d < distance:
                closest, distance = ("ship", ship), d
        # Ships drawn over an obstacle retain selection priority.
        if closest is not None:
            return closest
        distance = 17.0
        for obj in self.state.get("obstacles", []):
            shape, points = self.obstacle_screen_shape(obj)
            d = self.obstacle_distance(shape, points, x, y)
            if d < distance:
                closest, distance = ("obstacle", obj), d
        return closest if closest else (None, None)

    def select(self, owner, additive):
        if not additive:
            self.selected.clear()
        if owner is not None:
            if owner not in self.selected:
                self.selected.append(owner)
            elif additive:
                self.selected.remove(owner)
        self.draw()
        self.update_sidebar()

    def select_all(self, _event=None):
        if self.state:
            self.selected = [squad["owner"] for squad in self.state["squads"] if squad["controllable"]]
            self.draw()
            self.update_sidebar()

    def update_sidebar(self):
        if self.state is None:
            return
        self.sidebar_updating = True
        try:
            self.sidebar_owners = [
                s for s in self.state["squads"] if s["owned"]
            ]
            self.squad_list.delete(0, tk.END)
            for squad in self.sidebar_owners:
                owner = squad["owner"]
                number = squad.get("source_number")
                label = f"#{number}" if number is not None else f"ID {owner['squad_id']}"
                active = squad.get("command") or "Idle"
                self.squad_list.insert(tk.END,
                    f"{label:>6}  {len(squad['members']):>2} ships  {active}"
                    f"{' (orders disabled)' if not squad['controllable'] else ''}")
            for idx, squad in enumerate(self.sidebar_owners):
                if squad["owner"] in self.selected:
                    self.squad_list.selection_set(idx)
            details = [
                s for s in self.sidebar_owners if s["owner"] in self.selected
            ]
            if details:
                self.squad_detail.set("\n".join(
                    f"Squad {s['owner']['squad_id']}: {s.get('command') or 'Idle'}; "
                    f"cease {s.get('cease_fire')}; chase {s.get('chasing')}; "
                    f"lock {s.get('locked_on')}; shooting {s.get('shooting')}"
                    for s in details[:3]
                ))
            else:
                self.squad_detail.set("Select a friendly squad to issue orders")
            first_owner = details[0]["owner"] if details else None
            if first_owner != self.strategy_owner:
                self.strategy_owner = first_owner
                if details and details[0].get("shooting") is not None:
                    self.strategy.set(str(details[0]["shooting"]))
        finally:
            self.sidebar_updating = False

    def sidebar_select(self, _event):
        if self.sidebar_updating:
            return
        self.selected = [
            self.sidebar_owners[index]["owner"]
            for index in self.squad_list.curselection()
            if index < len(self.sidebar_owners) and
            self.sidebar_owners[index]["controllable"]
        ]
        self.update_sidebar()
        self.draw()

    def selected_owners(self):
        if self.state is None:
            return []
        live = {tuple(sorted(squad["owner"].items())): squad["owner"]
                for squad in self.state["squads"] if squad["controllable"]}
        self.selected = [s for s in self.selected if tuple(sorted(s.items())) in live]
        return self.selected

    def send(self, kind, body):
        if not self.console.alive or self.state is None:
            self.notice("No connected native battle")
            return
        if self.visible_dialogue() is not None:
            self.notice("Press L to continue or K to dismiss campaign dialogue")
            return
        owners = self.selected_owners()
        if not owners:
            self.notice("Select a friendly squad first")
            return
        try:
            self.console.send((kind, body), self.state, owners)
            self.notice(f"Sent {kind} for {len(owners)} squad(s)")
        except (OSError, ValueError) as error:
            self.notice(f"Order failed: {error}")

    def issue_at(self, x, y, mode):
        kind, target = self.hit(x, y)
        if mode in ("move", "fire"):
            wx, wy = self.to_world(x, y)
            self.send("move" if mode == "move" else "fire",
                      {"destination" if mode == "move" else "target":
                       {"x": wx, "y": wy}})
        elif mode == "patrol":
            x, y = self.to_world(x, y)
            if self.patrol_start is None:
                self.patrol_start = (x, y)
                self.notice("Choose opposite patrol corner")
                return
            ax, ay = self.patrol_start
            self.send("support", {"action": {"Patrol": {
                "top_left": {"x": min(ax, x), "y": max(ay, y)},
                "bottom_right": {"x": max(ax, x), "y": min(ay, y)},
            }}})
            self.patrol_start = None
        elif mode in ENEMY_MANEUVERS or mode == "guard":
            if kind != "ship":
                self.notice("Select a visible ship to target")
                return
            target_squad = self.squad_for_ship(target["id"],
                friendly=(mode == "guard"))
            if target_squad:
                operation = ("Guard" if mode == "guard" else ENEMY_MANEUVERS[mode])
                self.send("support", {"action": {operation: {"target": target_squad["owner"]}}})
            else:
                self.notice("That ship is not a valid target for this order")
                return
        elif mode == "mine" and kind == "obstacle" and target["mining"]:
            self.send("support", {"action": {"Mining": {"asteroid_id": target["id"]}}})
        elif mode in ("heal", "retreat") and kind == "ship" and target["friendly"]:
            operation = ("Heal", "beehive_ids") if mode == "heal" else ("FullRetreat", "warp_gate_id")
            self.send("support", {"action": {operation[0]:
                      {operation[1]: [target["id"]] if mode == "heal" else target["id"]}}})
        else:
            self.notice("Select an eligible visible target")
            return
        self.mode = None

    def finish_held_fire(self):
        if not self.held_drag:
            return
        self.held_drag = False
        try:
            self.console.held_fire("end")
            self.notice("Held fire released")
        except (OSError, ValueError) as error:
            self.notice(f"Held-fire release: {error}")

    def left_down(self, event):
        self.drag_origin = (event.x, event.y)
        self.drag_box = None
        if self.mode == "held-fire":
            if self.visible_dialogue() is not None:
                self.notice("Finish campaign dialogue before firing")
                return
            try:
                x, y = self.to_world(event.x, event.y)
                self.console.held_fire("begin", {"x": x, "y": y},
                                       owners=self.selected_owners())
                self.held_drag = True
                self.last_held_aim_at = time.monotonic()
                self.notice("Holding fire — drag to aim; release to stop")
            except (OSError, ValueError) as error:
                self.notice(f"Held fire unavailable: {error}")

    def left_drag(self, event):
        if self.held_drag:
            moment = time.monotonic()
            # Keep Aim events below the host's bounded per-peer poll budget.
            if moment - self.last_held_aim_at >= 0.05:
                try:
                    x, y = self.to_world(event.x, event.y)
                    self.console.held_fire("aim", {"x": x, "y": y})
                    self.last_held_aim_at = moment
                except (OSError, ValueError) as error:
                    self.notice(f"Held-fire aim: {error}")
                    self.held_drag = False
                    self.mode = None
            return
        if self.drag_origin is None or self.mode is not None:
            return
        if max(abs(event.x - self.drag_origin[0]),
               abs(event.y - self.drag_origin[1])) < 5:
            return
        self.drag_box = (self.drag_origin[0], self.drag_origin[1], event.x, event.y)
        self.draw()

    def release_held_on_root(self, _event):
        if self.held_drag:
            self.finish_held_fire()
            self.mode = None
            self.drag_origin = None

    def left_up(self, event):
        if self.drag_origin is None:
            return
        self.drag_origin = None
        if self.held_drag:
            self.finish_held_fire()
            self.mode = None
            self.draw()
            return
        if self.mode == "held-fire":
            return
        if self.mode:
            self.issue_at(event.x, event.y, self.mode)
            self.draw()
            return
        additive = bool(event.state & 0x0001)
        if self.drag_box is not None and self.state is not None:
            x0, y0, x1, y1 = self.drag_box
            x0, x1 = sorted((x0, x1))
            y0, y1 = sorted((y0, y1))
            owners = []
            for ship in self.state["ships"]:
                if not ship["owned"]:
                    continue
                sx, sy = self.to_screen(ship["x"], ship["y"])
                if x0 <= sx <= x1 and y0 <= sy <= y1:
                    squad = self.squad_for_ship(ship["id"], owned=True)
                    if squad and squad["controllable"] and squad["owner"] not in owners:
                        owners.append(squad["owner"])
            if not additive:
                self.selected = owners
            else:
                for owner in owners:
                    if owner not in self.selected:
                        self.selected.append(owner)
            self.drag_box = None
            self.draw()
            self.update_sidebar()
            return
        kind, target = self.hit(event.x, event.y)
        if kind == "ship" and target["owned"]:
            squad = self.squad_for_ship(target["id"], owned=True)
            self.select(squad["owner"] if squad and squad["controllable"] else None, additive)
        elif not additive:
            self.select(None, False)

    def context_click(self, event):
        if not self.selected_owners():
            return
        if self.mode:
            self.issue_at(event.x, event.y, self.mode)
            return
        kind, target = self.hit(event.x, event.y)
        if kind == "ship":
            # Ally allegiance is distinct from player control. Allied AI
            # squads can be guarded but must never be attacked or selected
            # as though their command permissions belonged to this player.
            squad = self.squad_for_ship(target["id"],
                                        friendly=target["friendly"])
            if squad and not target["friendly"]:
                self.send("support", {"action": {"Attack": {"target": squad["owner"]}}})
                return
            if squad and target["friendly"] and squad["owner"] not in self.selected:
                self.send("support", {"action": {"Guard": {"target": squad["owner"]}}})
                return
        if kind == "obstacle" and target["mining"]:
            self.send("support", {"action": {"Mining": {"asteroid_id": target["id"]}}})
            return
        wx, wy = self.to_world(event.x, event.y)
        self.send("move", {"destination": {"x": wx, "y": wy}})

    def draw(self):
        self.canvas.delete("all")
        state = self.state
        if state is None or self.map_bounds is None:
            return
        self.view_width = self.canvas.winfo_width()
        self.view_height = self.canvas.winfo_height()
        (x0, y0), (x1, y1) = self.map_bounds
        left, top = self.to_screen(x0, y1)
        right, bottom = self.to_screen(x1, y0)
        self.canvas.create_rectangle(left, top, right, bottom, outline="#40546c", width=2)
        # These destinations are public objective geometry from the host.
        # Rendering never guesses target areas from visible ship positions.
        for field, caption, color in (
            ("escort_destination", "Escort destination", FRIENDLY),
            ("extraction_destination", "Extraction zone", MINERAL),
        ):
            destination = state.get(field)
            if destination is None:
                continue
            (ax, ay), (bx, by) = destination
            px, py = self.to_screen(ax, ay)
            qx, qy = self.to_screen(bx, by)
            left, right = sorted((px, qx))
            top, bottom = sorted((py, qy))
            self.canvas.create_rectangle(left, top, right, bottom,
                                         outline=color, dash=(7, 4), width=2)
            self.canvas.create_text(left + 4, top + 4, text=caption, anchor="nw",
                                    fill=color)
        objective = state.get("campaign_objective") or {}
        demolition_target = (objective.get("target_map_object_id")
                             if objective.get("kind") == "demolition" else None)
        for obj in state.get("obstacles", []):
            shape, points = self.obstacle_screen_shape(obj)
            color = MINERAL if obj["mining"] else NEUTRAL
            if shape == "polygon":
                self.canvas.create_polygon(*points, outline=color, fill="", width=2)
                xs, ys = points[::2], points[1::2]
                extent = (min(xs), min(ys), max(xs), max(ys))
            elif shape == "oval":
                self.canvas.create_oval(*points, outline=color, width=2)
                extent = points
            elif shape == "bounds":
                self.canvas.create_rectangle(*points, outline=color,
                                             dash=(4, 3), width=2)
                extent = points
            else:
                x, y = points
                self.canvas.create_oval(x-5, y-5, x+5, y+5, outline=color, width=2)
                extent = (x-5, y-5, x+5, y+5)
            if obj["id"] == demolition_target:
                ax, ay, bx, by = extent
                self.canvas.create_rectangle(ax-8, ay-8, bx+8, by+8,
                                             outline=ENEMY, dash=(4, 3), width=2)
        for shot in state.get("projectiles", []):
            x, y = self.to_screen(shot["x"], shot["y"])
            self.canvas.create_oval(x-2, y-2, x+2, y+2, fill="#ffe4a2", outline="")
        selected = self.selected_owners()
        selected_ship_ids = {
            sid for squad in state["squads"] if squad["owner"] in selected
            for sid in squad["members"]
        }
        protected_id = (objective.get("protected_ship_id")
                        if objective.get("kind") == "survival" else
                        objective.get("escorted_ship_id")
                        if objective.get("kind") == "escort" else None)
        for ship in state["ships"]:
            x, y = self.to_screen(ship["x"], ship["y"])
            # Rotation comes from the authoritative native ship motion.
            angle = math.radians(ship.get("rotation", 0.0))
            sn, cs = math.sin(angle), math.cos(angle)
            triangle = []
            for local_x, local_y in ((0, 11), (-7, -7), (7, -7)):
                triangle.extend((x + local_x * cs + local_y * sn,
                                 y + local_x * sn - local_y * cs))
            color = FRIENDLY if ship["friendly"] else ENEMY
            # Controlled ships retain the strong outline; allied AI hulls
            # remain visibly friendly without acquiring player selection.
            self.canvas.create_polygon(*triangle, fill=color,
                                       outline=OUTLINE if ship["owned"] else "")
            if ship["id"] in selected_ship_ids:
                self.canvas.create_oval(x-14, y-14, x+14, y+14, outline="#ffffff", width=2)
            if ship["id"] == protected_id:
                self.canvas.create_oval(x-19, y-19, x+19, y+19,
                                        outline=MINERAL, dash=(4, 3), width=2)
            health = max(0.0, min(1.0, ship["health"] / max(1, ship["max_health"])))
            if ship["id"] in selected_ship_ids and ship.get("destination"):
                tx, ty = self.to_screen(*ship["destination"])
                self.canvas.create_line(x, y, tx, ty, fill="#748aaf", dash=(3, 4))
                self.canvas.create_line(tx-5, ty, tx+5, ty, fill="#d3e5ff")
                self.canvas.create_line(tx, ty-5, tx, ty+5, fill="#d3e5ff")
            self.canvas.create_rectangle(x-10, y+15, x+10, y+18, fill="#422d3b", outline="")
            self.canvas.create_rectangle(x-10, y+15, x-10+20*health, y+18, fill="#75d79c", outline="")
        if self.drag_box:
            self.canvas.create_rectangle(*self.drag_box, outline="#bce4ff", dash=(5,3))
        if self.patrol_start is not None:
            x, y = self.to_screen(*self.patrol_start)
            self.canvas.create_oval(x-8, y-8, x+8, y+8,
                                    outline=FRIENDLY, dash=(4, 2), width=2)
        info = f"Tick {state['tick']}  Side {state['viewer_side']}  Friendly squads {len(selected)}"
        final = self.console.battle_receipt_snapshot()
        outcome = state.get("terminal_outcome")
        if final is not None and final["kind"] == "battle_result":
            # The reliable receipt is sent after the durable campaign /
            # battle commit, even if the last world_state was superseded.
            outcome = {"label": final["label"], "reason": final["reason"]}
        elif final is not None:
            outcome = {"label": "Battle interrupted", "reason": final["reason"]}
        if outcome is not None:
            info += f"  |  {outcome['label']}"
        elif state["terminal_result"] is not None:
            info += f"  |  Winner: side {state['terminal_result']} (uncommitted)"
        if self.mode and outcome is None:
            info += f"  |  Targeting: {self.mode}"
        self.canvas.create_text(14, 14, text=info, anchor="nw", fill="#e7efff",
                                font=("TkDefaultFont", 11, "bold"))
        if outcome is not None:
            self.canvas.create_text(14, 38, text=outcome["reason"], anchor="nw",
                                    fill="#e7efff",
                                    width=max(200, self.canvas.winfo_width() - 28))
        else:
            objective_text = self.console.objective_summary(state)
            if objective_text is not None:
                self.canvas.create_text(
                    14, 38, text=f"Objective: {objective_text}", anchor="nw",
                    fill=MINERAL, width=max(200, self.canvas.winfo_width() - 28),
                    font=("TkDefaultFont", 10, "bold"),
                )
        dialogue = self.visible_dialogue()
        if dialogue is not None:
            speaker, line_text = dialogue
            width, height = self.canvas.winfo_width(), self.canvas.winfo_height()
            top = max(72, height - 160)
            self.canvas.create_rectangle(10, top, width - 10, height - 10,
                                         fill=BACKGROUND, outline=OUTLINE, width=2)
            self.canvas.create_text(24, top + 13, text=speaker, anchor="nw",
                                    fill=FRIENDLY, font=("TkDefaultFont", 12, "bold"))
            self.canvas.create_text(24, top + 43, text=line_text, anchor="nw",
                                    width=max(100, width - 50), fill="#e7efff",
                                    font=("TkDefaultFont", 11))
            self.canvas.create_text(24, height - 26, text="L next line  |  K dismiss",
                                    anchor="nw", fill=NEUTRAL)

    def close(self):
        if self.held_drag:
            self.finish_held_fire()
        self.console.alive = False
        try:
            self.connection.shutdown(socket.SHUT_RDWR)
        except OSError:
            pass
        self.connection.close()
        self.root.destroy()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("host")
    parser.add_argument("port", type=int)
    parser.add_argument("--side", type=int, choices=(1, 2),
                        help="authenticated player side for multiplayer")
    args = parser.parse_args()
    try:
        token = remote_token(args.side)
    except ValueError as error:
        parser.error(str(error))
    connection = socket.create_connection((args.host, args.port), timeout=10)
    connection.settimeout(None)
    try:
        console = RemoteConsole(connection, silent=True, expected_side=args.side)
        connection.sendall((json.dumps({"kind": "hello", "token": token}) + "\n").encode("utf-8"))
        threading.Thread(target=console.receive, daemon=True).start()
        root = tk.Tk()
        RemoteBattleWindow(root, console, connection)
        root.mainloop()
    finally:
        connection.close()


if __name__ == "__main__":
    main()
