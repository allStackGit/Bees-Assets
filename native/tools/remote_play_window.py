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
import tkinter as tk
from tkinter import ttk

from remote_play_client import RemoteConsole


BACKGROUND = "#101925"
FRIENDLY = "#67b9ff"
ENEMY = "#f0a365"
NEUTRAL = "#8a96a8"
MINERAL = "#d9c778"
OUTLINE = "#cad7e6"
MODES = {
    "Move": "move", "Attack": "attack", "Guard": "guard",
    "Mine": "mine", "Heal": "heal", "Retreat": "retreat",
    "Point fire": "fire",
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
        self.canvas = tk.Canvas(self.frame, bg=BACKGROUND, highlightthickness=0)
        self.canvas.pack(fill="both", expand=True)
        self.footer = ttk.Frame(self.frame, padding=6)
        self.footer.pack(fill="x")
        self.status = tk.StringVar(value="Connecting to native host...")
        ttk.Label(self.footer, textvariable=self.status).pack(side="left")
        self.info = tk.StringVar(value="Left-click select, Shift+click add, right-click context order")
        ttk.Label(self.footer, textvariable=self.info).pack(side="right")

        self.mode = None
        self.state = None
        self.drawn_scope = None
        self.map_bounds = None
        self.camera = None
        self.zoom = 1.0
        self.offset = (0.0, 0.0)
        self.drag_origin = None
        self.drag_box = None
        self.mouse_middle = None
        self.selected = []
        self.last_feedback = ""
        self.view_width = 1
        self.view_height = 1

        for title in ("Move", "Attack", "Guard", "Mine", "Heal", "Retreat", "Point fire"):
            ttk.Button(self.toolbar, text=title, command=lambda t=title: self.set_mode(MODES[t])
                       ).pack(side="left", padx=2)
        for title, action in [
            ("Stop fire", lambda: self.send("fire", {"target": None})),
            ("Cease", lambda: self.send("cease_fire", {"cease_fire": True})),
            ("Fire at will", lambda: self.send("cease_fire", {"cease_fire": False})),
            ("Hold", lambda: self.send("support", {"action": "Hold"})),
            ("Beacon", lambda: self.send("special", {"kind": "DropBeacon"})),
            ("Detonate", lambda: self.send("special", {"kind": "Detonate"})),
            ("Charge", lambda: self.send("special", {"kind": "Charge"})),
            ("Fit", self.fit),
        ]:
            ttk.Button(self.toolbar, text=title, command=action
                       ).pack(side="left", padx=2)

        self.canvas.bind("<ButtonPress-1>", self.left_down)
        self.canvas.bind("<B1-Motion>", self.left_drag)
        self.canvas.bind("<ButtonRelease-1>", self.left_up)
        self.canvas.bind("<Button-3>", self.context_click)
        self.canvas.bind("<MouseWheel>", self.wheel)
        self.canvas.bind("<Button-4>", lambda e: self.zoom_at(e, 1.2))
        self.canvas.bind("<Button-5>", lambda e: self.zoom_at(e, 1 / 1.2))
        self.canvas.bind("<ButtonPress-2>", self.pan_start)
        self.canvas.bind("<B2-Motion>", self.pan_move)
        self.canvas.bind("<Configure>", self.refresh)
        self.root.bind("<Escape>", lambda e: self.set_mode(None))
        self.root.bind("<Control-a>", self.select_all)
        self.root.protocol("WM_DELETE_WINDOW", self.close)
        self.root.after(50, self.poll)

    def notice(self, message):
        self.last_feedback = message
        self.status.set(message)

    def set_mode(self, mode):
        self.mode = mode
        self.notice(f"Select a {'map point' if mode in ('move', 'fire') else 'visible target'}"
                    f" for {mode}" if mode else "Order mode cancelled")

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

    def poll(self):
        while not self.console.notifications.empty():
            self.notice(self.console.notifications.get_nowait())
        state = self.console.snapshot()
        if state is not None:
            key = (state["world_id"], state["episode_revision"], state["tick"])
            if key != self.drawn_scope:
                self.state = state
                self.drawn_scope = key
                bounds = state.get("map_bounds")
                if self.map_bounds != bounds:
                    self.map_bounds = bounds
                    self.fit()
                else:
                    self.draw()
                if not self.last_feedback:
                    self.status.set(f"World {key[0]}, episode {key[1]}, tick {key[2]}")
        if self.console.alive:
            self.root.after(50, self.poll)
        else:
            self.notice("Disconnected from host; battle is no longer interactive")

    def refresh(self, _event=None):
        self.draw()

    def squad_for_ship(self, ship_id, *, owned=None):
        if self.state is None:
            return None
        for squad in self.state["squads"]:
            if ship_id in squad["members"] and (owned is None or squad["owned"] == owned):
                return squad
        return None

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
        for obj in self.state.get("obstacles", []):
            sx, sy = self.to_screen(obj["x"], obj["y"])
            d = (sx - x) ** 2 + (sy - y) ** 2
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

    def select_all(self, _event=None):
        if self.state:
            self.selected = [squad["owner"] for squad in self.state["squads"] if squad["owned"]]
            self.draw()

    def selected_owners(self):
        if self.state is None:
            return []
        live = {tuple(sorted(squad["owner"].items())): squad["owner"]
                for squad in self.state["squads"] if squad["owned"]}
        self.selected = [s for s in self.selected if tuple(sorted(s.items())) in live]
        return self.selected

    def send(self, kind, body):
        if not self.console.alive or self.state is None:
            self.notice("No connected native battle")
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
        elif mode in ("attack", "guard") and kind == "ship":
            target_squad = self.squad_for_ship(target["id"],
                owned=(mode == "guard"))
            if target_squad:
                operation = "Attack" if mode == "attack" else "Guard"
                self.send("support", {"action": {operation: {"target": target_squad["owner"]}}})
            else:
                self.notice("That ship is not a valid target for this order")
        elif mode == "mine" and kind == "obstacle" and target["mining"]:
            self.send("support", {"action": {"Mining": {"asteroid_id": target["id"]}}})
        elif mode in ("heal", "retreat") and kind == "ship" and target["owned"]:
            operation = ("Heal", "beehive_ids") if mode == "heal" else ("FullRetreat", "warp_gate_id")
            self.send("support", {"action": {operation[0]:
                      {operation[1]: [target["id"]] if mode == "heal" else target["id"]}}})
        else:
            self.notice("Select an eligible visible target")
            return
        self.mode = None

    def left_down(self, event):
        self.drag_origin = (event.x, event.y)
        self.drag_box = None

    def left_drag(self, event):
        if self.drag_origin is None or self.mode is not None:
            return
        if max(abs(event.x - self.drag_origin[0]),
               abs(event.y - self.drag_origin[1])) < 5:
            return
        self.drag_box = (self.drag_origin[0], self.drag_origin[1], event.x, event.y)
        self.draw()

    def left_up(self, event):
        if self.drag_origin is None:
            return
        start = self.drag_origin
        self.drag_origin = None
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
                    if squad and squad["owner"] not in owners:
                        owners.append(squad["owner"])
            if not additive:
                self.selected = owners
            else:
                for owner in owners:
                    if owner not in self.selected:
                        self.selected.append(owner)
            self.drag_box = None
            self.draw()
            return
        kind, target = self.hit(event.x, event.y)
        if kind == "ship" and target["owned"]:
            squad = self.squad_for_ship(target["id"], owned=True)
            self.select(squad["owner"] if squad else None, additive)
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
            squad = self.squad_for_ship(target["id"], owned=target["owned"])
            if squad and not target["owned"]:
                self.send("support", {"action": {"Attack": {"target": squad["owner"]}}})
                return
            if squad and target["owned"] and squad["owner"] not in self.selected:
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
        for obj in state.get("obstacles", []):
            x, y = self.to_screen(obj["x"], obj["y"])
            color = MINERAL if obj["mining"] else NEUTRAL
            self.canvas.create_oval(x-5, y-5, x+5, y+5, outline=color, width=2)
        for shot in state.get("projectiles", []):
            x, y = self.to_screen(shot["x"], shot["y"])
            self.canvas.create_oval(x-2, y-2, x+2, y+2, fill="#ffe4a2", outline="")
        selected = self.selected_owners()
        selected_ship_ids = {
            sid for squad in state["squads"] if squad["owner"] in selected
            for sid in squad["members"]
        }
        for ship in state["ships"]:
            x, y = self.to_screen(ship["x"], ship["y"])
            # Rotation comes from the authoritative native ship motion.
            angle = math.radians(ship.get("rotation", 0.0))
            sn, cs = math.sin(angle), math.cos(angle)
            triangle = []
            for local_x, local_y in ((0, 11), (-7, -7), (7, -7)):
                triangle.extend((x + local_x * cs + local_y * sn,
                                 y + local_x * sn - local_y * cs))
            color = FRIENDLY if ship["owned"] else ENEMY
            self.canvas.create_polygon(*triangle, fill=color, outline=OUTLINE if ship["owned"] else "")
            if ship["id"] in selected_ship_ids:
                self.canvas.create_oval(x-14, y-14, x+14, y+14, outline="#ffffff", width=2)
            health = max(0.0, min(1.0, ship["health"] / max(1, ship["max_health"])))
            self.canvas.create_rectangle(x-10, y+15, x+10, y+18, fill="#422d3b", outline="")
            self.canvas.create_rectangle(x-10, y+15, x-10+20*health, y+18, fill="#75d79c", outline="")
        if self.drag_box:
            self.canvas.create_rectangle(*self.drag_box, outline="#bce4ff", dash=(5,3))
        info = f"Tick {state['tick']}  Side {state['viewer_side']}  Friendly squads {len(selected)}"
        if state["terminal_result"] is not None:
            info += f"  |  Winner: side {state['terminal_result']}"
        if self.mode:
            info += f"  |  Targeting: {self.mode}"
        self.canvas.create_text(14, 14, text=info, anchor="nw", fill="#e7efff",
                                font=("TkDefaultFont", 11, "bold"))

    def close(self):
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
    args = parser.parse_args()
    token = os.environ.get("BEES_NATIVE_REMOTE_TOKEN")
    if not token:
        parser.error("BEES_NATIVE_REMOTE_TOKEN is required")
    connection = socket.create_connection((args.host, args.port), timeout=10)
    connection.settimeout(None)
    try:
        console = RemoteConsole(connection, silent=True)
        connection.sendall((json.dumps({"kind": "hello", "token": token}) + "\n").encode("utf-8"))
        threading.Thread(target=console.receive, daemon=True).start()
        root = tk.Tk()
        RemoteBattleWindow(root, console, connection)
        root.mainloop()
    finally:
        connection.close()


if __name__ == "__main__":
    main()
