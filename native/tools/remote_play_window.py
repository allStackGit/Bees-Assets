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
from queue import Empty, SimpleQueue
import time
import tkinter as tk
from tkinter import ttk, messagebox

from remote_play_client import ENEMY_MANEUVERS, RemoteConsole, remote_token


BACKGROUND = "#101925"
FRIENDLY = "#67b9ff"
ENEMY = "#f0a365"
NEUTRAL = "#8a96a8"
MINERAL = "#d9c778"
OUTLINE = "#cad7e6"

# Tk has no per-item Canvas alpha. Quantize the host's actual fading
# debris opacity into 17 blended colors once, not during each redraw.
PIECE_FADE = tuple(
    "#%02x%02x%02x" % tuple(
        round(int(BACKGROUND[1+2*channel:3+2*channel], 16) * (1 - shade/16) +
              int(MINERAL[1+2*channel:3+2*channel], 16) * shade/16)
        for channel in range(3)
    )
    for shade in range(17)
)
MODES = {
    "Move": "move", "Attack": "attack", "Circle": "circle",
    "Right swipe": "right-swipe", "Left swipe": "left-swipe",
    "In and out": "in-and-out", "Guard": "guard",
    "Mine": "mine", "Heal": "heal", "Retreat": "retreat",
    "Point fire": "fire", "Patrol": "patrol",
    "Held fire": "held-fire",
}


class RemoteBattleWindow:
    def __init__(self, root, console, connection, reconnect=None):
        self.root = root
        self.console = console
        self.connection = connection
        self.connect_recovered_host = reconnect
        # Recovery transports run off the Tk event loop. A host restart must
        # not freeze drawing or allow a stale command through a new peer ID.
        self._reconnect_results = SimpleQueue()
        self._reconnect_inflight = False
        self._reconnect_attempts = 0
        self._reconnect_next_at = 0.0
        self._reconnect_deadline = None
        self._reconnect_disabled = False
        self._disconnection_reported = False
        self._authentication_started_at = None
        self._closing = False
        self.root.title("Bees — native spectator" if console.spectator
                        else "Bees — native cooperative battle")
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
        self.sidebar = ttk.Frame(self.battle_area, padding=6, width=285)
        self.sidebar.pack(side="right", fill="y")
        self.sidebar.pack_propagate(False)
        ttk.Label(self.sidebar, text="Player squads").pack(anchor="w")
        self.squad_list = tk.Listbox(self.sidebar, selectmode=tk.EXTENDED,
                                     exportselection=False, height=15, width=35)
        self.squad_list.pack(fill="both", expand=True)
        self.sidebar_owners = []
        self.sidebar_updating = False
        self.squad_list.bind("<<ListboxSelect>>", self.sidebar_select)
        self.squad_detail = tk.StringVar(value="Select a squad")
        ttk.Label(self.sidebar, textvariable=self.squad_detail, wraplength=262,
                  justify="left").pack(anchor="w", pady=(8, 0))
        self.shooting_priority_label = ttk.Label(
            self.sidebar, text="Shooting priority (1-40)")
        self.shooting_priority_label.pack(anchor="w", pady=(10, 0))
        self.strategy = tk.StringVar(value="1")
        self.strategy_owner = None
        self.strategy_picker = ttk.Combobox(
            self.sidebar, textvariable=self.strategy, width=7, state="readonly",
            values=[str(i) for i in range(1, 41)],
        )
        self.strategy_picker.pack(anchor="w")
        self.apply_shooting_button = ttk.Button(
            self.sidebar, text="Apply shooting priority",
            command=self.apply_shooting)
        self.apply_shooting_button.pack(anchor="w", pady=(4, 0))
        self.footer = ttk.Frame(self.frame, padding=6)
        self.footer.pack(fill="x")
        # Controls have their own row: source status and keyboard hints can
        # grow without pushing destructive campaign actions offscreen.
        self.footer_controls = ttk.Frame(self.footer)
        self.footer_controls.pack(fill="x")
        # Time controls belong to a separate compact row. A 720px window
        # must still show its campaign and match-result actions.
        self.footer_clock = ttk.Frame(self.footer)
        self.status = tk.StringVar(value="Connecting to native host...")
        self.rejoin_button = ttk.Button(
            self.footer_controls, text="Rejoin recovered battle", command=self.rejoin,
            state="disabled",
        )
        self.rejoin_button.pack(side="left", padx=10)
        self.pause_button = ttk.Button(
            self.footer_clock, text="Pause", command=self.toggle_pause,
            state="disabled",
        )
        self.step_button = ttk.Button(
            self.footer_clock, text="Step tick", command=lambda:
            self.request_host_clock("step"), state="disabled",
        )
        self.speed_button = ttk.Button(
            self.footer_clock, text="Cycle speed", command=lambda:
            self.request_host_clock("speed"), state="disabled",
        )
        self.lobby_button = ttk.Button(
            self.footer_clock, text="Ready to play", command=self.ready_lobby,
            state="disabled",
        )
        self.claim_button = ttk.Button(
            self.footer_clock, text="Claim forfeit", command=self.claim_forfeit,
            state="disabled",
        )
        if not console.spectator:
            self.footer_clock.pack(fill="x", pady=(3, 0))
            for button in (self.pause_button, self.step_button, self.speed_button):
                button.pack(side="left", padx=3)
        self.surrender_button = ttk.Button(
            self.footer_controls, text="Concede battle", command=self.surrender,
            state="disabled",
        )
        if not console.spectator:
            self.surrender_button.pack(side="left", padx=10)
        self.retreat_button = ttk.Button(
            self.footer_controls, text="Open retreat exit", command=self.activate_retreat,
            state="disabled",
        )
        if not console.spectator:
            self.retreat_button.pack(side="left", padx=10)
        self.codex_button = ttk.Button(
            self.footer_controls, text="Fleet Codex", command=self.show_codex,
            state="disabled",
        )
        if not console.spectator:
            self.codex_button.pack(side="left", padx=10)
        self.campaign_details_button = ttk.Button(
            self.footer_controls, text="Campaign results",
            command=self.show_campaign_details, state="disabled",
        )
        if not console.spectator:
            self.campaign_details_button.pack(side="left", padx=10)
        self.info = tk.StringVar(value=(
            "Read-only faction view — wheel zoom, middle-drag pan"
            if console.spectator else
            "Left-click select, Ctrl/Shift+click toggle, right-click order"
        ))
        ttk.Label(self.footer, textvariable=self.status, wraplength=850,
                  justify="left").pack(fill="x", anchor="w", pady=(4, 0))
        ttk.Label(self.footer, textvariable=self.info, wraplength=850,
                  justify="left").pack(fill="x", anchor="w")

        self.mode = None
        self.patrol_start = None
        self.held_drag = False
        self.last_held_aim_at = 0.0
        self.state = None
        self.drawn_scope = None
        self.shown_terminal_receipt = None
        self.shown_campaign_receipt = None
        self.map_bounds = None
        self.camera = None
        self.zoom = 1.0
        self.offset = (0.0, 0.0)
        self.drag_origin = None
        self.drag_box = None
        self.mouse_middle = None
        self.minimap_dragging = False
        self.selected = []
        self.follow_ship_id = None
        self.last_ship_click = None
        # Presentation-only combat cues; the server owns every hit, death,
        # warped ship, and explosion, and has already filtered them by fog.
        self.visible_effects = []
        self.last_effect_redraw_at = 0.0
        self.weapon_overlay_on = tk.BooleanVar(value=False)
        self.last_feedback = ""
        self.dialogue_identity = None
        self.dialogue_line = 0
        self.view_width = 1
        self.view_height = 1
        self.performance_enabled = False
        self.performance_started_ns = time.perf_counter_ns()
        self.performance_counts = [0] * 12
        self.performance_label = ""

        if not console.spectator:
            ttk.Checkbutton(
                self.toolbar_tactics, text="Weapon ranges / contacts",
                variable=self.weapon_overlay_on,
                command=self.update_weapon_interest,
            ).pack(side="left", padx=4)
        # Keep time-critical targeting commands one click away, while
        # keeping *all* source actions accessible at 720px window width.
        # The old three fixed button rows overflowed narrow displays.
        for title in ("Move", "Attack", "Guard", "Mine", "Heal",
                      "Retreat", "Point fire", "Patrol", "Held fire"):
            ttk.Button(
                self.toolbar_main, text=title,
                command=lambda t=title: self.set_mode(MODES[t]),
            ).pack(side="left", padx=2)

        def dropdown(parent, title, entries):
            button = ttk.Menubutton(parent, text=title)
            menu = tk.Menu(button, tearoff=False)
            for caption, command in entries:
                menu.add_command(label=caption, command=command)
            button["menu"] = menu
            button.pack(side="left", padx=3)
            return button

        dropdown(self.toolbar_tactics, "Attack patterns", [
            (title, lambda t=title: self.set_mode(MODES[t]))
            for title in ("Circle", "Right swipe", "Left swipe", "In and out")
        ])
        for title, kind, body in [
            ("Stop fire", "fire", {"target": None}),
            ("Cease", "cease_fire", {"cease_fire": True}),
            ("Fire at will", "cease_fire", {"cease_fire": False}),
            ("Hold", "support", {"action": "Hold"}),
        ]:
            ttk.Button(self.toolbar_tactics, text=title,
                       command=lambda k=kind, v=body: self.send(k, v)
                       ).pack(side="left", padx=2)
        dropdown(self.toolbar_special, "Squad tactics", [
            ("Chase on", lambda: self.send("chase", {"chasing": True})),
            ("Chase off", lambda: self.send("chase", {"chasing": False})),
            ("Match speed", lambda: self.send("match_speed", {"match_speed": True})),
            ("Own speed", lambda: self.send("match_speed", {"match_speed": False})),
            ("Lock on", lambda: self.send("lock_on", {"locked": True})),
            ("Unlock", lambda: self.send("lock_on", {"locked": False})),
        ])
        dropdown(self.toolbar_special, "Ship abilities", [
            ("Beacon", lambda: self.send("special", {"kind": "DropBeacon"})),
            ("Detonate", lambda: self.send("special", {"kind": "Detonate"})),
            ("Charge", lambda: self.send("special", {"kind": "Charge"})),
        ])
        ttk.Button(self.toolbar_special, text="Fit map",
                   command=self.fit).pack(side="left", padx=2)

        if console.spectator:
            # Hide command affordances altogether. The view still uses the
            # existing native fog-filtered snapshot, zoom and pan paths.
            self.toolbar_main.pack_forget()
            self.toolbar_tactics.pack_forget()
            self.toolbar_special.pack_forget()
            self.shooting_priority_label.pack_forget()
            self.strategy_picker.pack_forget()
            self.apply_shooting_button.pack_forget()

        self.canvas.configure(takefocus=1)
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
        self.root.bind("<KeyPress>", self.squad_shortcut, add="+")
        self.root.bind("<KeyPress>", self.camera_shortcut, add="+")
        self.root.bind("<F2>", lambda _event: self.show_codex())
        self.root.bind("<F8>", self.toggle_performance)
        self.root.protocol("WM_DELETE_WINDOW", self.close)
        self.root.after(50, self.poll)

    def notice(self, message):
        self.last_feedback = message
        self.status.set(message)

    def show_codex(self):
        state = self.state
        if self.console.spectator or state is None:
            self.notice("Fleet Codex unavailable to this viewer")
            return
        entries = self.console.codex_entries(state)
        if entries is None:
            self.notice("This native host does not expose named Codex entries")
            return
        rows = []
        for entry in entries:
            live = entry.get("live")
            status = (f" | HP {live['health']}/{live['max_health']}"
                      if live is not None else "")
            if live is not None and live.get("tsv") is not None:
                status += f" | TSV {live['tsv']}/{live['max_tsv']}"
            rows.append(f"{entry['ship_type']:>2}  {entry['name']}{status}")
        messagebox.showinfo(
            "Fleet Codex",
            f"{len(rows)} source ship classes " +
            ("unlocked" if state.get("campaign_codex_gate") is True
             else "encountered") + "\n\n" +
            ("\n".join(rows) if rows else "No ship classes unlocked yet"),
            parent=self.root,
        )

    def show_campaign_details(self):
        if self.console.spectator:
            return
        receipt = self.console.campaign_receipt_snapshot()
        if receipt is None:
            self.notice("No committed campaign results for this battle")
            return
        classes = {
            entry["ship_type"]: entry["name"]
            for entry in self.console.codex_entries(self.state) or []
        }
        messagebox.showinfo(
            "Committed campaign results",
            self.console.campaign_reward_details(receipt, classes),
            parent=self.root,
        )

    def activate_retreat(self):
        state = self.state
        if (self.console.spectator or not self.console.alive or
                state is None or state.get("can_activate_retreat") is not True or
                state.get("briefing_paused") is True or
                state.get("terminal_outcome") is not None or self.held_drag or
                self.console.battle_receipt_snapshot() is not None):
            self.notice("Retreat exit cannot be opened in this battle state")
            return
        try:
            self.console.send(
                ("activate_retreat", {"side": state["viewer_side"]}), state, []
            )
            self.notice("Retreat exit activation submitted to native host")
        except (OSError, ValueError) as error:
            self.notice(f"Retreat activation unavailable: {error}")

    def surrender(self):
        if (self.console.spectator or not self.console.alive or
                self.state is None or self.state.get("can_surrender") is not True or
                self.state.get("briefing_paused") is True or
                self.state.get("terminal_outcome") is not None or
                self.held_drag):
            self.notice("Surrender is unavailable in this battle state")
            return
        if not messagebox.askyesno(
            "Concede battle",
            "Surrender this battle? The host will commit the defeat and "
            "campaign consequences. This cannot be undone.",
            icon="warning", parent=self.root,
        ):
            return
        try:
            self.console.surrender()
            self.notice("Surrender submitted; awaiting authoritative battle result")
            self.surrender_button.state(["disabled"])
        except (ValueError, OSError) as error:
            self.notice(f"Surrender unavailable: {error}")

    def ready_lobby(self):
        try:
            if self.console.acknowledge_lobby():
                self.notice("Waiting for the native host to confirm readiness")
        except (OSError, ValueError) as error:
            self.notice(f"Could not join the native match: {error}")

    def claim_forfeit(self):
        try:
            self.console.claim_forfeit(self.state)
            self.notice("Authoritative opponent-forfeit claim submitted")
        except (OSError, ValueError) as error:
            self.notice(f"Could not claim opponent forfeit: {error}")

    def request_host_clock(self, command):
        if not self.console.time_controls_available:
            self.notice("Dedicated native host clock controls are unavailable")
            return
        try:
            self.console.time_control(command, self.state)
        except (OSError, ValueError) as error:
            self.notice(f"Host clock request rejected: {error}")

    def toggle_pause(self):
        self.request_host_clock(
            "resume" if self.console.pause_owned else "pause"
        )

    def rejoin(self):
        # A manual attempt can restart an expired recovery window. All
        # attempts still require the original authenticated session ID.
        if self.console.alive or self.connect_recovered_host is None:
            return
        session = self.console.host_session_id or self.console.expected_session_id
        if session is None:
            self.notice("No authenticated native session is available to rejoin")
            return
        self._reconnect_disabled = False
        self._reconnect_deadline = time.monotonic() + 120.0
        self._reconnect_next_at = 0.0
        self._start_reconnect(session)

    def _start_reconnect(self, session):
        if (self._closing or self._reconnect_inflight or self.console.alive or
                self.connect_recovered_host is None):
            return
        self._reconnect_inflight = True
        self._reconnect_attempts += 1
        attempt_number = self._reconnect_attempts
        self.status.set(
            f"Reconnecting to original native battle (attempt {attempt_number})..."
        )

        def connect_off_thread():
            connection = None
            try:
                connection, console = self.connect_recovered_host(session)
                if self._closing:
                    connection.close()
                else:
                    self._reconnect_results.put((connection, console, None))
            except (OSError, ValueError) as error:
                if connection is not None:
                    connection.close()
                if not self._closing:
                    self._reconnect_results.put((None, None, str(error)))

        threading.Thread(target=connect_off_thread, daemon=True).start()

    def _receive_reconnect_results(self):
        while True:
            try:
                connection, console, failure = self._reconnect_results.get_nowait()
            except Empty:
                break
            self._reconnect_inflight = False
            if self._closing:
                if connection is not None:
                    connection.close()
                continue
            if failure is not None:
                self._reconnect_next_at = time.monotonic() + min(
                    15.0, 0.5 * (2 ** min(self._reconnect_attempts, 5))
                )
                self.status.set(f"Original host unavailable: {failure}")
                continue
            self._install_reconnected_peer(connection, console)

    def _install_reconnected_peer(self, connection, console):
        # A successful TCP dial is NOT a successful recovery. RemoteConsole
        # validates the welcome's host_session_id and side before accepting
        # world_state; until then no input is enabled and retries remain
        # limited by the original recovery deadline.
        old_connection = self.connection
        self.console = console
        self.connection = connection
        self.state = None
        self.drawn_scope = None
        self.shown_terminal_receipt = None
        self.shown_campaign_receipt = None
        self.map_bounds = None
        self.dialogue_identity = None
        self.dialogue_line = 0
        self.selected = []
        self.follow_ship_id = None
        self.last_ship_click = None
        self.minimap_dragging = False
        self.visible_effects = []
        self.weapon_overlay_on.set(False)
        self.patrol_start = None
        self.held_drag = False
        self.mode = None
        self.last_feedback = ""
        self.canvas.delete("all")
        self.squad_list.delete(0, "end")
        self.squad_detail.set("Waiting for authoritative roster")
        self.rejoin_button.state(["disabled"])
        self._authentication_started_at = time.monotonic()
        self.status.set("Authenticating against the original host session...")
        old_connection.close()

    def set_mode(self, mode):
        if self.console.spectator:
            self.notice("Read-only spectator: commands unavailable")
            return
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
        self.follow_ship_id = None
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
        self.follow_ship_id = None
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
        self.follow_ship_id = None
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
        self._receive_reconnect_results()
        if self._authentication_started_at is not None:
            if self.console.host_session_id is not None:
                self._authentication_started_at = None
            elif (self.console.alive and
                  time.monotonic() - self._authentication_started_at > 10.0):
                # TCP connected, but the host never authenticated this
                # particular World. Do not accept gameplay or wait forever.
                try:
                    self.connection.shutdown(socket.SHUT_RDWR)
                except OSError:
                    pass
                self._authentication_started_at = None
        while not self.console.notifications.empty():
            self.notice(self.console.notifications.get_nowait())
        state = self.console.snapshot()
        if state is not None:
            # A dead connection retains its last read-only snapshot. Only a
            # live, authenticated new socket can reset the recovery deadline.
            if self.console.alive and self.console.host_session_id is not None:
                self._reconnect_deadline = None
                self._reconnect_attempts = 0
                self._disconnection_reported = False
            key = (state["world_id"], state["episode_revision"], state["tick"])
            # A paused or not-yet-started host may publish new authorized
            # lobby, time-control or briefing state without advancing a
            # physical tick. Object identity detects a fresh wire snapshot
            # without comparing hundreds of ships or allocating a diff.
            if key != self.drawn_scope or state is not self.state:
                if self.drawn_scope is not None and (
                    key[0] != self.drawn_scope[0] or
                    key[1] != self.drawn_scope[1]
                ):
                    self.selected = []
                    self.follow_ship_id = None
                    self.last_ship_click = None
                    self.minimap_dragging = False
                    self.visible_effects = []
                    self.weapon_overlay_on.set(False)
                    self.patrol_start = None
                    self.held_drag = False
                    self.mode = None
                self.state = state
                self.drawn_scope = key
                if self.follow_ship_id is not None:
                    # Visibility is authoritative and per-episode. Never
                    # extrapolate a lost or undisclosed unit position.
                    followed = next((ship for ship in state["ships"]
                                     if ship["id"] == self.follow_ship_id), None)
                    if followed is None:
                        self.follow_ship_id = None
                    else:
                        self.center_camera_on(followed["x"], followed["y"],
                                              redraw=False)
                now = time.monotonic()
                self.visible_effects = [
                    (started, duration, event)
                    for started, duration, event in self.visible_effects
                    if now - started < duration
                ]
                for event in state.get("audio_events", []):
                    effect = event.get("effect")
                    if (effect in ("impact", "huge_explosion", "warp",
                                   "striker_bomb", "obstacle_debris") or
                            event.get("ship_death") is not None):
                        duration = (0.9 if event.get("ship_death") is not None
                                    else 0.7 if effect == "huge_explosion"
                                    else 0.45)
                        self.visible_effects.append((now, duration, event))
                # The authoritative host already bounds audio events.
                # Further bound presentation history during sustained fire.
                if len(self.visible_effects) > 96:
                    del self.visible_effects[:-96]
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
                    ping = self.console.heartbeat_rtt_ms
                    latency = f" | LAN {ping:.0f} ms" if ping is not None else ""
                    self.status.set(
                        f"World {key[0]}, episode {key[1]}, tick {key[2]}{latency}"
                    )
        lobby = state.get("lobby") if state else None
        lobby_waiting = isinstance(lobby, dict) and lobby.get("waiting") is True
        if not self.console.spectator and lobby_waiting:
            if not self.lobby_button.winfo_manager():
                self.lobby_button.pack(side="left", padx=7)
            already_ready = (self.console.lobby_ready_owned or
                             self.console.lobby_ready_request is not None)
            self.lobby_button.state(
                ["disabled"] if already_ready or not self.console.alive
                else ["!disabled"]
            )
            self.lobby_button.configure(
                text="Ready confirmed" if self.console.lobby_ready_owned
                else "Awaiting ready ACK" if self.console.lobby_ready_request
                else "Ready to play"
            )
            if not self.last_feedback:
                self.status.set(
                    f"Waiting for both players — sides ready: "
                    f"{lobby.get('ready_sides', [])} / "
                    f"{lobby.get('required_sides', [])}"
                )
        elif self.lobby_button.winfo_manager():
            self.lobby_button.pack_forget()
        reconnect_waiting = state is not None and (
            state.get("waiting_for_player_reconnect") is True
        )
        if reconnect_waiting and not self.last_feedback:
            self.status.set(
                "Match paused for player reconnection — connected sides: "
                f"{state.get('connected_commander_sides', [])}"
            )
        claim_available = (
            state is not None and state.get("forfeit_claim_available") is True and
            self.console.alive and not self.console.spectator
        )
        if claim_available:
            if not self.claim_button.winfo_manager():
                self.claim_button.pack(side="left", padx=7)
            self.claim_button.state(
                ["disabled"] if self.console.forfeit_request_id is not None
                else ["!disabled"]
            )
        elif self.claim_button.winfo_manager():
            self.claim_button.pack_forget()
        can_host_clock = (
            not self.console.spectator and self.console.alive and
            self.console.time_controls_available and state is not None and
            state.get("terminal_outcome") is None and
            self.console.battle_receipt_snapshot() is None
        )
        self.pause_button.configure(
            text="Resume" if self.console.pause_owned else "Pause"
        )
        self.pause_button.state(
            ["!disabled"] if can_host_clock else ["disabled"]
        )
        self.step_button.state(
            ["!disabled"] if can_host_clock and self.console.pause_owned
            else ["disabled"]
        )
        speed = state.get("game_speed") if state else None
        self.speed_button.configure(
            text=f"Cycle speed ({speed:g}x)"
            if isinstance(speed, (int, float)) and math.isfinite(speed)
            else "Cycle speed"
        )
        self.speed_button.state(
            ["!disabled"] if can_host_clock else ["disabled"]
        )
        can_surrender = (
            not self.console.spectator and self.console.alive and
            state is not None and state.get("can_surrender") is True and
            state.get("briefing_paused") is not True and
            state.get("terminal_outcome") is None and not self.held_drag and
            self.console.surrender_requested_episode != (
                state["world_id"], state["episode_revision"]
            )
        )
        self.surrender_button.state(
            ["!disabled"] if can_surrender else ["disabled"]
        )
        can_retreat = (
            not self.console.spectator and self.console.alive and
            state is not None and state.get("can_activate_retreat") is True and
            state.get("briefing_paused") is not True and
            state.get("terminal_outcome") is None and not reconnect_waiting and
            self.console.battle_receipt_snapshot() is None and not self.held_drag
        )
        self.retreat_button.state(
            ["!disabled"] if can_retreat else ["disabled"]
        )
        has_codex = (
            not self.console.spectator and state is not None and
            (state.get("campaign_codex_entries") is not None or
             state.get("visible_ship_classes") is not None)
        )
        self.codex_button.state(
            ["!disabled"] if has_codex else ["disabled"]
        )
        has_campaign_results = (
            not self.console.spectator and
            self.console.campaign_receipt_snapshot() is not None
        )
        self.campaign_details_button.state(
            ["!disabled"] if has_campaign_results else ["disabled"]
        )
        now = time.monotonic()
        if self.visible_effects:
            self.visible_effects = [
                (started, duration, event)
                for started, duration, event in self.visible_effects
                if now - started < duration
            ]
            if self.visible_effects and now - self.last_effect_redraw_at >= 0.1:
                self.last_effect_redraw_at = now
                self.draw()
        terminal = self.console.battle_receipt_snapshot()
        campaign_receipt = self.console.campaign_receipt_snapshot()
        if (terminal != self.shown_terminal_receipt or
                campaign_receipt != self.shown_campaign_receipt):
            self.shown_terminal_receipt = terminal
            self.shown_campaign_receipt = campaign_receipt
            self.draw()
        if not self.console.alive:
            session = self.console.host_session_id or self.console.expected_session_id
            terminal = self.console.battle_receipt_snapshot()
            campaign = self.console.campaign_receipt_snapshot()
            outcome = self.state.get("terminal_outcome") if self.state else None
            if session is not None and self.connect_recovered_host is not None:
                self.rejoin_button.state(["!disabled"])
            self.held_drag = False
            if not self._disconnection_reported:
                self._disconnection_reported = True
                if terminal is not None:
                    self.notice(
                        f"Battle finished: {terminal['label']} — {terminal['reason']}"
                        if terminal["kind"] == "battle_result"
                        else f"Battle interrupted: {terminal['reason']}"
                    )
                elif outcome is not None:
                    self.notice("Host closed before a reliable final battle receipt")
                else:
                    self.notice("Connection lost; preserving the original session for recovery")
                if campaign is not None:
                    self.notice(self.console.campaign_receipt_summary(campaign))
                self.draw()

            if self.console.protocol_failure is not None:
                # A different host, wrong side or corrupt protocol is never
                # silently accepted as the original game.
                self._reconnect_disabled = True
                self.status.set("Native session authentication rejected; manual retry only")
            elif (session and terminal is None and not self._reconnect_disabled and
                  self.connect_recovered_host is not None):
                if self._reconnect_deadline is None:
                    self._reconnect_deadline = now + 120.0
                if now >= self._reconnect_deadline:
                    self._reconnect_disabled = True
                    self.status.set("Recovery window expired; retry manually")
                elif not self._reconnect_inflight and now >= self._reconnect_next_at:
                    self._start_reconnect(session)
        if not self._closing:
            self.root.after(50, self.poll)

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

    def collider_screen_shape(self, obj, override=None):
        """Project a host-owned ship or obstacle collider with its live pose."""
        collider = override if override is not None else obj.get("collider") or {}
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

    # Preserve a single geometry projector for map objects and physical
    # ships; no remote-only collision or steering model is introduced.
    def obstacle_screen_shape(self, obj):
        return self.collider_screen_shape(obj)

    def collider_on_screen(self, obj, margin=24):
        """Skip drawing remote physical hulls outside the current viewport.

        CombatWorld's already-cached world AABB encloses rotated and
        irregular shapes, so offscreen pruning cannot hide an intersecting
        hull even when its centre is far beyond the camera.
        """
        bounds = obj.get("collider_bounds")
        if bounds is None:
            return True
        ax, ay = self.to_screen(*bounds[0])
        bx, by = self.to_screen(*bounds[1])
        width = self.canvas.winfo_width()
        height = self.canvas.winfo_height()
        return (max(ax, bx) >= -margin and min(ax, bx) <= width + margin and
                max(ay, by) >= -margin and min(ay, by) <= height + margin)

    @staticmethod
    def collider_screen_bounds(shape, points):
        if shape in ("oval", "bounds"):
            return points
        if shape == "polygon":
            return min(points[::2]), min(points[1::2]), max(points[::2]), max(points[1::2])
        x, y = points
        return x-4, y-4, x+4, y+4

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

    @classmethod
    def collider_overlaps_selection(cls, shape, points, rect):
        """True where a real rendered hull intersects the selection box.

        The local native client uses physics collider overlap, not ship
        centre containment. This screen-space query uses the identical
        host-projected hull; it never steps or modifies game physics.
        """
        x0, y0, x1, y1 = rect
        left, top, right, bottom = cls.collider_screen_bounds(shape, points)
        if right < x0 or left > x1 or bottom < y0 or top > y1:
            return False
        if shape in ("bounds", "point"):
            return True
        if shape == "oval":
            # Rotated source circles remain circles. The generalized
            # ellipse form also works for rectangular display scaling.
            cx, cy = (left+right)*0.5, (top+bottom)*0.5
            rx, ry = (right-left)*0.5, (bottom-top)*0.5
            if rx <= 0 or ry <= 0:
                return False
            px = min(max(cx, x0), x1)
            py = min(max(cy, y0), y1)
            return ((px-cx)/rx)**2 + ((py-cy)/ry)**2 <= 1
        vertices = list(zip(points[::2], points[1::2]))
        if not vertices:
            return False
        if any(x0 <= x <= x1 and y0 <= y <= y1 for x, y in vertices):
            return True
        # A rectangle may lie entirely inside a large concave polygon.
        if any(cls.obstacle_distance("polygon", points, x, y) == 0
               for x, y in ((x0,y0), (x1,y0), (x1,y1), (x0,y1))):
            return True

        def crosses(a, b, c, d):
            def orient(p, q, r):
                return (q[0]-p[0])*(r[1]-p[1]) - (q[1]-p[1])*(r[0]-p[0])
            o1, o2 = orient(a,b,c), orient(a,b,d)
            o3, o4 = orient(c,d,a), orient(c,d,b)
            return o1*o2 <= 0 and o3*o4 <= 0 and (
                max(a[0],b[0]) >= min(c[0],d[0]) and
                max(c[0],d[0]) >= min(a[0],b[0]) and
                max(a[1],b[1]) >= min(c[1],d[1]) and
                max(c[1],d[1]) >= min(a[1],b[1])
            )
        corners = ((x0,y0), (x1,y0), (x1,y1), (x0,y1))
        return any(crosses(a, b, corners[j], corners[(j+1) % 4])
                   for i, a in enumerate(vertices)
                   for b in (vertices[(i+1) % len(vertices)],)
                   for j in range(4))

    def hit(self, x, y):
        if self.state is None:
            return None, None
        closest = None
        distance = 17.0
        for ship in self.state["ships"]:
            shape, points = self.collider_screen_shape(ship)
            d = self.obstacle_distance(shape, points, x, y)
            # A small screen-space tolerance allows selecting thin hulls,
            # even at low zoom; the full real hull is clickable.
            if d < distance:
                closest, distance = ("ship", ship), d
            # Only clicks, not drag-box commands or physical combat, use
            # the authored Beehive HealCollider child. The host projects
            # the real source shape; a remote client never guesses its size.
            healing = ship.get("input_healing_zone")
            if healing is not None:
                zone_shape, zone_points = self.collider_screen_shape(ship, healing)
                zone_distance = self.obstacle_distance(zone_shape, zone_points, x, y)
                if zone_distance < distance:
                    closest, distance = ("ship", ship), zone_distance
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

    def update_weapon_interest(self):
        if self.console.spectator or not self.console.alive or self.state is None:
            return
        owners = self.selected_owners() if self.weapon_overlay_on.get() else []
        try:
            self.console.request_weapon_overlay(self.state, owners)
        except (OSError, ValueError) as error:
            self.weapon_overlay_on.set(False)
            self.notice(f"Weapon overlay unavailable: {error}")
        self.draw()

    def publish_selection(self):
        # Source story scripts (including Pluto Reinforcements) consume
        # actual Select inputs. Highlighting in Tk alone cannot advance
        # the authoritative campaign; submit the exact live lifetimes.
        if self.console.spectator or not self.console.alive or self.state is None:
            return
        try:
            self.console.submit_selection(self.state, self.selected_owners())
        except (OSError, ValueError) as error:
            self.notice(f"Squad selection not delivered: {error}")

    def select(self, owner, additive):
        if not additive:
            self.selected.clear()
        if owner is not None:
            if owner not in self.selected:
                self.selected.append(owner)
            elif additive:
                self.selected.remove(owner)
        self.publish_selection()
        if self.weapon_overlay_on.get():
            self.update_weapon_interest()
        self.draw()
        self.update_sidebar()

    def focus_squad(self, owner):
        if self.state is None:
            return
        squad = next((s for s in self.state["squads"]
                      if s["owner"] == owner), None)
        if squad is None:
            return
        members = set(squad["members"])
        bounds = []
        for ship in self.state["ships"]:
            if ship["id"] not in members:
                continue
            hull = ship.get("collider_bounds")
            if hull is not None:
                bounds.append(hull)
            else:
                bounds.append([[ship["x"], ship["y"]], [ship["x"], ship["y"]]])
        if not bounds:
            return
        cx = (min(h[0][0] for h in bounds) + max(h[1][0] for h in bounds)) / 2
        cy = (min(h[0][1] for h in bounds) + max(h[1][1] for h in bounds)) / 2
        px, py = self.to_screen(cx, cy)
        self.offset = (self.offset[0] + self.canvas.winfo_width()/2 - px,
                       self.offset[1] + self.canvas.winfo_height()/2 - py)

    def squad_shortcut(self, event):
        # Tk propagates keypresses from comboboxes to root. Only the
        # focused tactical map consumes numbered or cyclic squad shortcuts.
        if (event.widget is not self.canvas or self.console.spectator or
                not self.console.alive or self.state is None or
                self.held_drag or self.visible_dialogue() is not None):
            return
        if len(event.char) == 1 and event.char in "0123456789":
            owner = self.console.source_number_owner(self.state, int(event.char))
        elif event.keysym in ("Prior", "Next"):
            owner = self.console.cycle_source_owner(
                self.state, self.selected[0] if self.selected else None,
                event.keysym == "Next",
            )
        else:
            return
        if owner is None:
            return
        self.selected = [owner]
        self.publish_selection()
        self.focus_squad(owner)
        if self.weapon_overlay_on.get():
            self.update_weapon_interest()
        self.update_sidebar()
        self.draw()
        return "break"

    def select_all(self, _event=None):
        if self.state and not self.console.spectator:
            # Source Ctrl+A selects root player squads, not dependent
            # Carrier/Queen children already represented by their parents.
            self.selected = [
                squad["owner"] for squad in self.state["squads"]
                if squad["controllable"] and squad.get("source_root") is True
            ]
            self.publish_selection()
            if self.weapon_overlay_on.get():
                self.update_weapon_interest()
            self.draw()
            self.update_sidebar()

    def update_sidebar(self):
        if self.state is None:
            return
        self.sidebar_updating = True
        try:
            known_classes = {
                entry["ship_type"]: entry["name"]
                for entry in self.console.codex_entries(self.state) or []
            }
            self.sidebar_owners = [
                s for s in self.state["squads"] if s["owned"]
            ]
            self.squad_list.delete(0, tk.END)
            for squad in self.sidebar_owners:
                owner = squad["owner"]
                number = squad.get("source_number")
                label = f"#{number}" if number is not None else f"ID {owner['squad_id']}"
                active = squad.get("command") or "Idle"
                lead = squad.get("lead_ship_type")
                ship_class = known_classes.get(lead, f"Type {lead}" if lead is not None else "Fleet")
                self.squad_list.insert(tk.END,
                    f"{label:>6}  {len(squad['members']):>2} ships  {ship_class}  {active}"
                    f"{' (orders disabled)' if not squad['controllable'] else ''}")
            for idx, squad in enumerate(self.sidebar_owners):
                if squad["owner"] in self.selected:
                    self.squad_list.selection_set(idx)
            details = [
                s for s in self.sidebar_owners if s["owner"] in self.selected
            ]
            if details:
                owned_ships = {
                    ship["id"]: ship for ship in self.state["ships"] if ship["owned"]
                }
                lines = []
                total_health = total_max_health = total_tsv = total_base = total_cargo = 0
                total_members = 0
                for row in details:
                    ships = [owned_ships[ship_id] for ship_id in row["members"]
                             if ship_id in owned_ships]
                    health = sum(ship["health"] for ship in ships)
                    max_health = sum(ship["max_health"] for ship in ships)
                    tsv = sum(ship.get("tsv") or 0 for ship in ships)
                    base = sum(ship.get("base_tsv") or 0 for ship in ships)
                    cargo = sum(ship.get("mined_cargo") or 0 for ship in ships)
                    total_health += health
                    total_max_health += max_health
                    total_tsv += tsv
                    total_base += base
                    total_cargo += cargo
                    total_members += len(ships)
                    if len(lines) < 3:
                        lines.append(
                            f"Squad {row['owner']['squad_id']} ({len(ships)} ships)"
                            f"  HP {health}/{max_health}"
                            f"  TSV {tsv} (base {base})  Mined {cargo}\n"
                            f"{row.get('command') or 'Idle'}; "
                            f"cease {row.get('cease_fire')}; "
                            f"chase {row.get('chasing')}; "
                            f"lock {row.get('locked_on')}; "
                            f"shooting {row.get('shooting')}"
                        )
                if len(details) > 1:
                    lines.insert(0,
                        f"Selected {len(details)} squads / {total_members} ships\n"
                        f"HP {total_health}/{total_max_health}  "
                        f"TSV {total_tsv} (base {total_base})  Mined {total_cargo}")
                if len(details) > 3:
                    lines.append(f"…and {len(details)-3} more selected squads")
                self.squad_detail.set("\n\n".join(lines))
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
        self.publish_selection()
        if self.weapon_overlay_on.get():
            self.update_weapon_interest()
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
        if self.console.spectator:
            self.notice("Read-only spectator: commands unavailable")
            return
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

    def camera_shortcut(self, event):
        # Match native desktop camera controls without stealing editing
        # shortcuts from the sidebar's comboboxes and input widgets.
        if event.widget is not self.canvas:
            return
        if event.keysym.lower() == "f":
            self.fit()
            return "break"
        movement = {
            "Left": (65, 0), "Right": (-65, 0),
            "Up": (0, 65), "Down": (0, -65),
        }.get(event.keysym)
        if movement is None:
            return
        self.follow_ship_id = None
        self.offset = (self.offset[0] + movement[0],
                       self.offset[1] + movement[1])
        self.draw()
        return "break"

    def minimap_geometry(self):
        """Map the host's actual world bounds into a compact overview."""
        if self.map_bounds is None:
            return None
        width = self.canvas.winfo_width()
        height = self.canvas.winfo_height()
        if width < 320 or height < 260:
            return None
        (x0, y0), (x1, y1) = self.map_bounds
        if x1 <= x0 or y1 <= y0:
            return None
        scale = min(210 / (x1-x0), 150 / (y1-y0))
        size_x, size_y = (x1-x0)*scale, (y1-y0)*scale
        left = 12
        top = height - size_y - 12
        return (left, top, left+size_x, top+size_y, scale)

    def minimap_world_at(self, x, y):
        geometry = self.minimap_geometry()
        if geometry is None:
            return None
        left, top, right, bottom, scale = geometry
        if not left <= x <= right or not top <= y <= bottom:
            return None
        (x0, _), (_, y1) = self.map_bounds
        return (x0 + (x-left)/scale, y1 - (y-top)/scale)

    def center_camera_on(self, world_x, world_y, *, redraw=True):
        x, y = self.to_screen(world_x, world_y)
        width, height = self.canvas.winfo_width(), self.canvas.winfo_height()
        self.offset = (self.offset[0] + width*0.5 - x,
                       self.offset[1] + height*0.5 - y)
        if redraw:
            self.draw()

    def minimap_pan(self, event):
        target = self.minimap_world_at(event.x, event.y)
        if target is not None:
            self.follow_ship_id = None
            self.center_camera_on(*target)

    def draw_combat_cues(self):
        now = time.monotonic()
        for started, duration, event in self.visible_effects:
            progress = (now-started)/duration
            if not 0 <= progress < 1:
                continue
            death = event.get("ship_death")
            effect = event.get("effect")
            x, y = self.to_screen(event["x"], event["y"])
            if not -120 <= x <= self.view_width+120 or not -120 <= y <= self.view_height+120:
                continue
            if death is not None:
                # Physical death hull extent is supplied by CombatWorld;
                # the expanding circle is a visual cue, not an AoE model.
                world_radius = max(0.0, death.get("radius", 0.0))
                radius = min(90.0, max(8.0,
                    world_radius * self.transform()[0] * (0.5+progress)))
                color = "#ffcc78"
            elif effect == "huge_explosion":
                radius = 9 + 36 * progress
                color = "#f0a365"
            elif effect == "warp":
                radius = 6 + 25 * progress
                color = FRIENDLY
            elif effect == "obstacle_debris":
                radius = 7 + 20 * progress
                color = MINERAL
            elif effect == "striker_bomb":
                radius = 6 + 10 * progress
                color = ENEMY
            else:
                radius = 4 + 11 * progress
                color = "#ffe4a2"
            self.canvas.create_oval(
                x-radius, y-radius, x+radius, y+radius,
                outline=color, dash=(3, 3) if progress > 0.6 else (),
                width=2 if progress < 0.5 else 1,
            )

    def draw_minimap(self, state, selected_ship_ids):
        geometry = self.minimap_geometry()
        if geometry is None:
            return
        left, top, right, bottom, scale = geometry
        self.canvas.create_rectangle(left, top, right, bottom,
                                     fill=BACKGROUND, outline=OUTLINE, width=2)
        (x0, _), (_, y1) = self.map_bounds

        def point(x, y):
            return left+(x-x0)*scale, top+(y1-y)*scale

        # The authored Level supplies actual objective areas, including
        # retreat corridors that exist only after activation. A minimap
        # never invents destinations from current fleet positions.
        for field, color in (
            ("escort_destination", FRIENDLY),
            ("extraction_destination", MINERAL),
        ):
            area = state.get(field)
            if area is None:
                continue
            (ax, ay), (bx, by) = area
            px, py = point(ax, ay)
            qx, qy = point(bx, by)
            lx, rx = max(left, min(px, qx)), min(right, max(px, qx))
            ty, by = max(top, min(py, qy)), min(bottom, max(py, qy))
            if lx <= rx and ty <= by:
                self.canvas.create_rectangle(
                    lx, ty, rx, by, outline=color, dash=(3, 2),
                )

        objective = state.get("campaign_objective") or {}
        objective_kind = objective.get("kind")
        target_object = (objective.get("target_map_object_id")
                         if objective_kind == "demolition" else None)
        important_ship_ids = {
            value for value in (
                objective.get("protected_ship_id"),
                objective.get("escorted_ship_id"),
                objective.get("target_ship_id"),
            ) if value is not None
        }
        important_ship_ids.update(objective.get("barge_ids") or [])
        # Mission identity does not imply current visibility. Coordinates
        # come solely from already-filtered physical ship/map-object rows.
        for obj in state.get("obstacles", []):
            if obj["id"] != target_object:
                continue
            px, py = point(obj["x"], obj["y"])
            if left <= px <= right and top <= py <= bottom:
                self.canvas.create_rectangle(px-4, py-4, px+4, py+4,
                                             outline=ENEMY, width=2)

        # The host has already fog-culled all static physical objects.
        # Show cover, solid terrain and minerals; the local native minimap
        # must not imply a clear route through an undisclosed obstacle.
        # Deduplicate subpixel markers to keep crowded battles cheap.
        cells = set()
        for obj in state.get("obstacles", []):
            px, py = point(obj["x"], obj["y"])
            if not left <= px <= right or not top <= py <= bottom:
                continue
            color = MINERAL if obj.get("mining") else NEUTRAL
            bounds = obj.get("collider_bounds")
            if bounds is not None:
                (a, b), (c, d) = bounds
                ax, ay = point(a, d)
                bx, by = point(c, b)
                ax, bx = max(left, ax), min(right, bx)
                ay, by = max(top, ay), min(bottom, by)
                if bx-ax > 4 and by-ay > 4:
                    self.canvas.create_rectangle(
                        ax, ay, bx, by, outline=color,
                    )
                    continue
            cell = (int(px), int(py), 0)
            if cell in cells:
                continue
            cells.add(cell)
            self.canvas.create_rectangle(px-1, py-1, px+1, py+1,
                                         fill=color, outline="")
        for ship in state["ships"]:
            px, py = point(ship["x"], ship["y"])
            if not left <= px <= right or not top <= py <= bottom:
                continue
            cell = (int(px), int(py), 1 if ship["friendly"] else 2)
            if cell not in cells:
                cells.add(cell)
                self.canvas.create_oval(px-1.5, py-1.5, px+1.5, py+1.5,
                    fill=FRIENDLY if ship["friendly"] else ENEMY, outline="")
            if ship["id"] in selected_ship_ids:
                self.canvas.create_oval(px-3, py-3, px+3, py+3,
                                        outline="#ffffff")
            if ship["id"] in important_ship_ids:
                self.canvas.create_rectangle(px-5, py-5, px+5, py+5,
                                             outline=MINERAL if ship["friendly"]
                                             else ENEMY, width=2)
        # Show actual camera coverage. Clamping keeps outlines inside
        # the minimap even while the player pans beyond world edges.
        wa, wb = self.to_world(0, 0), self.to_world(
            self.canvas.winfo_width(), self.canvas.winfo_height()
        )
        ax, ay = point(min(wa[0], wb[0]), max(wa[1], wb[1]))
        bx, by = point(max(wa[0], wb[0]), min(wa[1], wb[1]))
        ax, bx = max(left, ax), min(right, bx)
        ay, by = max(top, ay), min(bottom, by)
        if ax < bx and ay < by:
            self.canvas.create_rectangle(ax, ay, bx, by,
                                         outline="#d3e5ff", dash=(3, 2))

    def left_down(self, event):
        self.canvas.focus_set()
        if self.minimap_world_at(event.x, event.y) is not None:
            self.minimap_dragging = True
            self.minimap_pan(event)
            return
        if self.console.spectator:
            kind, target = self.hit(event.x, event.y)
            self.follow_ship_id = (target["id"] if kind == "ship" else None)
            if self.follow_ship_id is not None:
                self.center_camera_on(target["x"], target["y"])
                self.notice(f"Following visible ship {self.follow_ship_id}")
            else:
                self.draw()
            return
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
        if self.minimap_dragging:
            self.minimap_pan(event)
            return
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
        self.minimap_dragging = False
        if self.held_drag:
            self.finish_held_fire()
            self.mode = None
            self.drag_origin = None

    def left_up(self, event):
        if self.minimap_dragging:
            self.minimap_dragging = False
            return
        if self.console.spectator:
            return
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
        # Tk uses Shift=0x0001 and Control=0x0004 on Windows/X11. Match
        # the native window's Ctrl-click squad toggle while keeping the
        # remote client's established Shift-click shortcut.
        additive = bool(event.state & (0x0001 | 0x0004))
        if self.drag_box is not None and self.state is not None:
            x0, y0, x1, y1 = self.drag_box
            x0, x1 = sorted((x0, x1))
            y0, y1 = sorted((y0, y1))
            owners = []
            for ship in self.state["ships"]:
                if not ship["owned"]:
                    continue
                shape, points = self.collider_screen_shape(ship)
                if self.collider_overlaps_selection(shape, points, (x0, y0, x1, y1)):
                    squad = self.squad_for_ship(ship["id"], owned=True)
                    if squad and squad["controllable"] and squad["owner"] not in owners:
                        owners.append(squad["owner"])
            if not additive:
                self.selected = owners
            else:
                for owner in owners:
                    if owner not in self.selected:
                        self.selected.append(owner)
            self.publish_selection()
            self.drag_box = None
            self.last_ship_click = None
            self.draw()
            self.update_sidebar()
            return
        kind, target = self.hit(event.x, event.y)
        if kind == "ship" and target["owned"]:
            squad = self.squad_for_ship(target["id"], owned=True)
            if squad is None or not squad["controllable"]:
                return
            now = time.monotonic()
            double = (not additive and self.last_ship_click is not None and
                      self.last_ship_click[0] == target["id"] and
                      now - self.last_ship_click[1] <= 0.35)
            if double:
                # Match native SelectShipType: source-order selectable squads
                # containing at least one *live* hull of this exact type.
                # The host retains all actual mission selection authority.
                kind = target["ship_type"]
                visible_types = {ship["id"]: ship["ship_type"]
                                 for ship in self.state["ships"] if ship["owned"]}
                self.selected = [
                    row["owner"] for row in self.state["squads"]
                    if row["controllable"] and any(
                        visible_types.get(ship_id) == kind for ship_id in row["members"])
                ]
                self.publish_selection()
                if self.weapon_overlay_on.get():
                    self.update_weapon_interest()
                self.update_sidebar()
                self.draw()
                self.last_ship_click = None
            else:
                self.select(squad["owner"], additive)
                self.last_ship_click = (target["id"], now)
        else:
            self.last_ship_click = None
            if not additive:
                self.select(None, False)

    def context_click(self, event):
        destination = self.minimap_world_at(event.x, event.y)
        if destination is not None:
            # Match the local native minimap: right click issues a real
            # Move command to the world point, never a separate minimap order.
            if not self.console.spectator and self.selected_owners():
                self.send("move", {"destination": {
                    "x": destination[0], "y": destination[1],
                }})
            else:
                self.center_camera_on(*destination)
            return
        if self.console.spectator:
            return
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

    def toggle_performance(self, _event=None):
        self.performance_enabled = not self.performance_enabled
        self.performance_started_ns = time.perf_counter_ns()
        self.performance_counts = [0] * 12
        self.performance_label = ""
        self.draw()

    def draw(self):
        self.canvas.delete("all")
        state = self.state
        if state is None or self.map_bounds is None:
            return
        # Measure only requested presentation frames. Never insert timing
        # calls into the game tick or infer CPU/GPU utilization from redraws.
        measuring = self.performance_enabled
        began_ns = time.perf_counter_ns() if measuring else None
        ships_drawn = ships_culled = obstacles_drawn = obstacles_culled = shots_drawn = 0
        self.view_width = self.canvas.winfo_width()
        self.view_height = self.canvas.winfo_height()
        (x0, y0), (x1, y1) = self.map_bounds
        left, top = self.to_screen(x0, y1)
        right, bottom = self.to_screen(x1, y0)
        self.canvas.create_rectangle(left, top, right, bottom, outline="#40546c", width=2)
        # Side-authenticated fog sources come from the native combat
        # observer. This visual backdrop cannot reveal any undisclosed
        # objects: the host still filters all ships, projectiles, shards,
        # asteroids and squad targets before serialization.
        fog_circles_drawn = fog_circles_culled = 0
        if state.get("fog_enabled"):
            world_scale = self.transform()[0]
            for source in state.get("fog_sources", []):
                x, y = self.to_screen(source["x"], source["y"])
                radius = source["radius"] * world_scale
                if (x+radius < 0 or y+radius < 0 or
                        x-radius > self.view_width or
                        y-radius > self.view_height):
                    if measuring:
                        fog_circles_culled += 1
                    continue
                if measuring:
                    fog_circles_drawn += 1
                self.canvas.create_oval(
                    x-radius, y-radius, x+radius, y+radius,
                    fill="#1b2d3d", outline="",
                )
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
            if not self.collider_on_screen(obj):
                if measuring:
                    obstacles_culled += 1
                continue
            if measuring:
                obstacles_drawn += 1
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
            # A map-object health bar is only legal when the authoritative
            # host explicitly disclosed max_health. Never estimate private
            # asteroid durability from its prefab, kind, or outline.
            maximum = obj.get("max_health")
            health = obj.get("health")
            if maximum is not None and health is not None and maximum > 0:
                ax, ay, bx, by = extent
                width = max(22.0, min(64.0, bx-ax))
                mx, my = (ax+bx)*0.5, by+5
                remaining = max(0.0, min(1.0, health/maximum))
                self.canvas.create_rectangle(mx-width/2, my, mx+width/2, my+3,
                                             fill="#422d3b", outline="")
                self.canvas.create_rectangle(mx-width/2, my,
                                             mx-width/2+width*remaining, my+3,
                                             fill=MINERAL if obj["mining"] else ENEMY,
                                             outline="")
        pieces_drawn = pieces_culled = 0
        for piece in state.get("asteroid_pieces", []):
            # These IDs, current poses, and fade fractions are from the
            # authoritative CombatWorld, and were already fog-filtered
            # for this viewer. Map glyphs are visual only; not colliders.
            x, y = self.to_screen(piece["x"], piece["y"])
            if not (-7 <= x <= self.view_width+7 and
                    -7 <= y <= self.view_height+7):
                if measuring:
                    pieces_culled += 1
                continue
            shade = max(0, min(16, round(piece["opacity"] * 16)))
            if shade == 0:
                continue
            if measuring:
                pieces_drawn += 1
            angle = math.radians(piece["rotation"])
            sn, cs = math.sin(angle), math.cos(angle)
            coords = []
            for dx, dy in ((-4, -2), (3, -3), (5, 2), (-3, 4)):
                coords.extend((x + dx * cs - dy * sn,
                               y + dx * sn + dy * cs))
            self.canvas.create_polygon(*coords, fill=PIECE_FADE[shade],
                                       outline="")

        shots_culled = 0
        for shot in state.get("projectiles", []):
            if not self.collider_on_screen(shot):
                if measuring:
                    shots_culled += 1
                continue
            # Live projectile hulls are read from the same physics state
            # as damage and collision. Laser beams grow, explosions expand,
            # and missile shapes turn without a client-side weapon model.
            shape, points = self.collider_screen_shape(shot)
            left, top, right, bottom = self.collider_screen_bounds(shape, points)
            if (right < -5 or left > self.view_width+5 or
                    bottom < -5 or top > self.view_height+5):
                if measuring:
                    shots_culled += 1
                continue
            if measuring:
                shots_drawn += 1
            projectile_type = shot.get("type")
            exploding = projectile_type in (13, 14, 15)
            color = ("#f0a365" if exploding else
                     "#f6ca8a" if projectile_type in (5, 12) else
                     "#ffe4a2")
            if shape == "polygon":
                self.canvas.create_polygon(*points,
                                           fill="" if exploding else color,
                                           outline=color)
            elif shape == "oval":
                # Subpixel bullets remain perceptible at battle-map zoom.
                cx, cy = (left+right)*0.5, (top+bottom)*0.5
                rx, ry = max(2.0, (right-left)*0.5), max(2.0, (bottom-top)*0.5)
                self.canvas.create_oval(cx-rx, cy-ry, cx+rx, cy+ry,
                                       fill="" if exploding else color,
                                       outline=color)
            elif shape == "bounds":
                self.canvas.create_rectangle(*points, outline=color)
            else:
                x, y = points
                self.canvas.create_oval(x-2, y-2, x+2, y+2,
                                        fill=color, outline="")
        selected = self.selected_owners()
        selected_ship_ids = {
            sid for squad in state["squads"] if squad["owner"] in selected
            for sid in squad["members"]
        }
        # The host discloses only actual visible enemy hull IDs currently
        # marked by the shared aggressive/bombing command director. Render
        # the same live targets without running a second target selector.
        visible_ships = {ship["id"]: ship for ship in state["ships"]}
        marked_targets = set()
        for squad in state["squads"]:
            if squad["owner"] not in selected:
                continue
            targets = [visible_ships[ship_id] for ship_id in squad.get("command_targets", [])
                       if ship_id in visible_ships]
            if not targets:
                continue
            marked_targets.update(ship["id"] for ship in targets)
            members = [visible_ships[ship_id] for ship_id in squad["members"]
                       if ship_id in visible_ships]
            if members:
                sx = sum(ship["x"] for ship in members) / len(members)
                sy = sum(ship["y"] for ship in members) / len(members)
                tx = sum(ship["x"] for ship in targets) / len(targets)
                ty = sum(ship["y"] for ship in targets) / len(targets)
                ax, ay = self.to_screen(sx, sy)
                bx, by = self.to_screen(tx, ty)
                self.canvas.create_line(ax, ay, bx, by,
                                        fill=ENEMY, dash=(5, 4), width=2)
        protected_id = (objective.get("protected_ship_id")
                        if objective.get("kind") == "survival" else
                        objective.get("escorted_ship_id")
                        if objective.get("kind") == "escort" else None)
        target_ship_id = (objective.get("target_ship_id")
                          if objective.get("kind") == "target_ship" else None)
        # Rescue identities are disclosed only to the genuine original
        # campaign player; never infer enemy objectives from visible types.
        rescue_ship_ids = set(objective.get("barge_ids") or []) if (
            objective.get("kind") == "barge_rescue"
        ) else set()
        for ship in state["ships"]:
            if not self.collider_on_screen(ship):
                if measuring:
                    ships_culled += 1
                continue
            if measuring:
                ships_drawn += 1
            x, y = self.to_screen(ship["x"], ship["y"])
            if self.weapon_overlay_on.get() and ship["id"] in selected_ship_ids:
                for weapon in ship.get("weapons", []):
                    ring = weapon.get("range")
                    if ring is not None and ring[3]:
                        center_x = ship["x"] + ring[0]
                        center_y = ship["y"] + ring[1]
                        cx, cy = self.to_screen(center_x, center_y)
                        rx, _ = self.to_screen(center_x + ring[2], center_y)
                        radius = abs(rx - cx)
                        self.canvas.create_oval(
                            cx-radius, cy-radius, cx+radius, cy+radius,
                            outline="#648eae", dash=(3, 4),
                        )
                    origin = weapon.get("origin")
                    if origin:
                        ax, ay = self.to_screen(*origin)
                        for contact in weapon.get("contacts", []):
                            bx, by = self.to_screen(contact[0], contact[1])
                            self.canvas.create_line(
                                ax, ay, bx, by,
                                fill=ENEMY if contact[2] else FRIENDLY,
                                dash=(4, 3) if contact[2] else (),
                            )
            color = FRIENDLY if ship["friendly"] else ENEMY
            # Map-scale silhouettes use the exact current physical collider
            # supplied by CombatWorld. Sprites remain the local native
            # renderer's responsibility; multiplayer targeting must never
            # substitute a generic triangle for a huge or irregular hull.
            shape, points = self.collider_screen_shape(ship)
            # Use the original SavedSquad color when provided by the
            # authenticated host, while keeping allegiance distinguishable
            # through the stable friendly/enemy fill colors.
            custom = ship.get("color_rgb")
            squad_color = ("#%02x%02x%02x" % tuple(custom)
                           if isinstance(custom, list) and len(custom) == 3
                           and all(isinstance(channel, int) and
                                   0 <= channel <= 255 for channel in custom)
                           else OUTLINE)
            outline = squad_color if ship["owned"] else color
            left, top, right, bottom = self.collider_screen_bounds(shape, points)
            if shape == "polygon":
                self.canvas.create_polygon(*points, fill=color, outline=outline)
            elif shape == "oval":
                self.canvas.create_oval(*points, fill=color, outline=outline)
            elif shape == "bounds":
                self.canvas.create_rectangle(*points, fill=color, outline=outline)
            else:
                px, py = points
                self.canvas.create_oval(px-4, py-4, px+4, py+4,
                                        fill=color, outline=outline)
            if ship["id"] in selected_ship_ids:
                if shape == "polygon":
                    self.canvas.create_polygon(*points, fill="", outline="#ffffff", width=2)
                elif shape == "oval":
                    self.canvas.create_oval(*points, outline="#ffffff", width=2)
                elif shape == "bounds":
                    self.canvas.create_rectangle(*points, outline="#ffffff", width=2)
                else:
                    self.canvas.create_oval(x-8, y-8, x+8, y+8,
                                            outline="#ffffff", width=2)
            if ship["id"] == self.follow_ship_id:
                self.canvas.create_oval(
                    left-8, top-8, right+8, bottom+8,
                    outline="#ffffff", dash=(4, 3), width=2,
                )
            if ship["id"] in marked_targets:
                self.canvas.create_rectangle(
                    left-4, top-4, right+4, bottom+4,
                    outline=ENEMY, dash=(3, 2), width=2)
            if ship["id"] == protected_id or ship["id"] in rescue_ship_ids:
                self.canvas.create_rectangle(left-5, top-5, right+5, bottom+5,
                                             outline=MINERAL, dash=(4, 3), width=2)
            if ship["id"] == target_ship_id:
                # Objective markers follow the true hull, not an arbitrary
                # circle that can disappear inside a large flagship.
                self.canvas.create_rectangle(
                    left-7, top-7, right+7, bottom+7,
                    outline=ENEMY if not ship["friendly"] else FRIENDLY,
                    dash=(5, 3), width=2)
            health = max(0.0, min(1.0, ship["health"] / max(1, ship["max_health"])))
            if ship["id"] in selected_ship_ids and ship.get("destination"):
                tx, ty = self.to_screen(*ship["destination"])
                self.canvas.create_line(x, y, tx, ty, fill="#748aaf", dash=(3, 4))
                self.canvas.create_line(tx-5, ty, tx+5, ty, fill="#d3e5ff")
                self.canvas.create_line(tx, ty-5, tx, ty+5, fill="#d3e5ff")
            # Place the status meter outside the real collider, with a
            # bounded display width so giant hulls do not fill the screen.
            meter_width = max(20.0, min(70.0, right-left))
            mx, my = (left+right)*0.5, bottom+5
            self.canvas.create_rectangle(mx-meter_width/2, my,
                                         mx+meter_width/2, my+3,
                                         fill="#422d3b", outline="")
            self.canvas.create_rectangle(mx-meter_width/2, my,
                                         mx-meter_width/2+meter_width*health, my+3,
                                         fill="#75d79c", outline="")
        if self.drag_box:
            self.canvas.create_rectangle(*self.drag_box, outline="#bce4ff", dash=(5,3))
        if self.patrol_start is not None:
            x, y = self.to_screen(*self.patrol_start)
            self.canvas.create_oval(x-8, y-8, x+8, y+8,
                                    outline=FRIENDLY, dash=(4, 2), width=2)
        self.draw_combat_cues()
        self.draw_minimap(state, selected_ship_ids)
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
        if self.follow_ship_id is not None:
            info += f"  |  Following ship {self.follow_ship_id}"
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

        # Reliable final receipts supersede replaceable live snapshots for
        # the finished match. Spectator receipts deliberately contain no
        # private player score/career totals; never derive them from hulls.
        statistics = final if final is not None and final["kind"] == "battle_result" else state
        parts = []
        if statistics.get("player_score") is not None:
            parts.append(f"Score {statistics['player_score']}")
        counts = statistics.get("player_match_counts") or {}
        for key, title in (("ships_destroyed_by_player", "Kills"),
                           ("ships_lost", "Lost"), ("ships_returned", "Returned")):
            if counts.get(key) is not None:
                parts.append(f"{title} {counts[key]}")
        mined = statistics.get("player_minerals_mined")
        if mined is None:
            mined = counts.get("player_minerals_mined")
        if mined is not None:
            parts.append(f"Mined {mined}")
        if parts:
            self.canvas.create_text(
                14, 63, text="  |  ".join(parts), anchor="nw", fill="#d3e5ff",
                font=("TkDefaultFont", 10, "bold"),
            )
        saved = self.console.campaign_receipt_snapshot()
        if saved is not None and not self.console.spectator:
            summary = self.console.campaign_receipt_summary(saved)
            delta = saved.get("campaign_score_delta")
            if delta is not None:
                summary += f"  |  Score delta {delta:+d}"
            self.canvas.create_text(
                14, 84, text=summary, anchor="nw", fill=MINERAL,
                width=max(200, self.canvas.winfo_width()-28),
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
        if began_ns is not None:
            elapsed_ns = time.perf_counter_ns() - began_ns
            stats = self.performance_counts
            for index, addition in enumerate((
                1, elapsed_ns, ships_drawn, ships_culled,
                obstacles_drawn, obstacles_culled, shots_drawn,
                pieces_drawn, pieces_culled, shots_culled,
                fog_circles_drawn, fog_circles_culled,
            )):
                stats[index] += addition
            sampled_ns = time.perf_counter_ns() - self.performance_started_ns
            if sampled_ns >= 1_000_000_000:
                frames = stats[0]
                self.performance_label = (
                    f"Map {frames * 1_000_000_000 / sampled_ns:.1f} redraws/s"
                    f"  |  build {stats[1] / frames / 1_000_000:.2f} ms\n"
                    f"Ships {stats[2] / frames:.0f} drawn / {stats[3] / frames:.0f} culled"
                    f"  |  objects {stats[4] / frames:.0f} drawn / {stats[5] / frames:.0f} culled"
                    f"  |  shots {stats[6] / frames:.0f} / {stats[9] / frames:.0f} culled"
                    f"  |  pieces {stats[7] / frames:.0f} / {stats[8] / frames:.0f} culled\n"
                    f"Vision circles {stats[10] / frames:.0f} / {stats[11] / frames:.0f} culled"
                )
                self.performance_started_ns = time.perf_counter_ns()
                self.performance_counts = [0] * 12
            if self.performance_label:
                self.canvas.create_text(
                    self.view_width-14, 14, text=self.performance_label,
                    anchor="ne", justify="right", fill="#d3e5ff",
                    font=("TkDefaultFont", 9, "bold"),
                )

    def close(self):
        self._closing = True
        while True:
            try:
                pending_connection, _, _ = self._reconnect_results.get_nowait()
            except Empty:
                break
            if pending_connection is not None:
                pending_connection.close()
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
                        help="authenticated faction for commanders or spectators")
    parser.add_argument("--spectator", action="store_true",
                        help="open a read-only faction-filtered native battle viewer")
    args = parser.parse_args()
    try:
        token = remote_token(args.side, spectator=args.spectator)
    except ValueError as error:
        parser.error(str(error))
    def connect(expected_session_id=None):
        # The new TCP peer is always freshly authenticated; only the
        # original checkpointed host session may resume the old battle.
        connection = socket.create_connection((args.host, args.port), timeout=10)
        connection.settimeout(None)
        try:
            console = RemoteConsole(
                connection, silent=True, expected_side=args.side,
                expected_session_id=expected_session_id,
                spectator=args.spectator,
            )
            connection.sendall(
                (json.dumps({"kind": "hello", "token": token,
                             "spectator": args.spectator,
                             "resume_session_id": expected_session_id}) + "\n").encode("utf-8")
            )
            threading.Thread(target=console.receive, daemon=True).start()
            return connection, console
        except BaseException:
            connection.close()
            raise

    connection, console = connect()
    window = None
    try:
        root = tk.Tk()
        window = RemoteBattleWindow(root, console, connection, reconnect=connect)
        root.mainloop()
    finally:
        (window.connection if window is not None else connection).close()


if __name__ == "__main__":
    main()
