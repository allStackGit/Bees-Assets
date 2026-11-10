//! Small, window-independent tactical renderer of authoritative live geometry.
//! Owns only pixels/camera/selection; it never mutates or steps a battle.

use std::collections::HashSet;
use std::io::{self, Write};
use std::sync::OnceLock;
use bees_sim::collision::{Aabb, Pose};
use bees_sim::geometry::Vec2;
use bees_sim::physics::ShipShape;
use super::{NativeRenderWorldView, NativeSquadSelection};

#[derive(Clone, Copy, Debug)]
pub struct NativeCamera {
    pub center: Vec2,
    pub pixels_per_unit: f32,
}

impl NativeCamera {
    pub fn fit(bounds: Aabb, width: usize, height: usize) -> Self {
        Self {
            center: (bounds.min + bounds.max) * 0.5,
            pixels_per_unit: ((width as f32 / (bounds.max.x - bounds.min.x).max(1.0))
                .min(height as f32 / (bounds.max.y - bounds.min.y).max(1.0)) * 0.9)
                .clamp(0.001, 1000.0),
        }
    }

    pub fn screen_to_world(self, x: f32, y: f32, width: usize, height: usize) -> Vec2 {
        self.center + Vec2::new(x - width as f32 * 0.5, height as f32 * 0.5 - y)
            * (1.0 / self.pixels_per_unit)
    }

    pub fn zoom_at(&mut self, factor: f32, x: f32, y: f32, width: usize, height: usize) {
        if !factor.is_finite() || factor <= 0.0 { return; }
        let before = self.screen_to_world(x, y, width, height);
        self.pixels_per_unit = (self.pixels_per_unit * factor).clamp(0.001, 1000.0);
        self.center += before - self.screen_to_world(x, y, width, height);
    }
}

#[derive(Clone, Copy, Debug, Default, serde::Serialize)]
pub struct NativeRenderCounts {
    pub ships: usize,
    pub obstacles: usize,
    pub culled_ships: usize,
    pub culled_obstacles: usize,
    pub projectiles: usize,
    pub pieces: usize,
    pub culled_projectiles: usize,
    pub culled_pieces: usize,
    pub range_queries: usize,
    pub range_contacts: usize,
    pub aim_points: usize,
    pub automatic_targets: usize,
    pub missing_aim_data: usize,
    pub range_overlaps: usize,
    pub obstructed_ranges: usize,
    pub missing_range_data: usize,
}

pub struct NativeSoftwareRenderer {
    width: usize,
    height: usize,
    pixels: Vec<u32>,
    pub camera: NativeCamera,
    /// Selection is scoped to a WorldId AND episode revision by the client.
    pub selected_ship: Option<i64>,
    pub selection: NativeSquadSelection,
    // Reused only for large selections; small squads need no hash allocation.
    selected_squad_lookup: HashSet<bees_hivemind::session::SquadLifetime>,
    pub selection_box: Option<Aabb>,
    /// Optional inspection only. Disabled means no range or linecast work.
    pub show_weapon_ranges: bool,
}

impl NativeSoftwareRenderer {
    pub fn new(width: usize, height: usize, bounds: Aabb) -> io::Result<Self> {
        if width == 0 || height == 0 || width > 8192 || height > 8192 ||
            !bounds.min.is_finite() || !bounds.max.is_finite() ||
            bounds.min.x >= bounds.max.x || bounds.min.y >= bounds.max.y {
            return Err(io::Error::new(io::ErrorKind::InvalidInput, "invalid viewport or bounds"));
        }
        Ok(Self { width, height, pixels: vec![0; width * height],
            camera: NativeCamera::fit(bounds, width, height), selected_ship: None,
            selection: NativeSquadSelection::default(),
            selected_squad_lookup: HashSet::new(),
            selection_box: None, show_weapon_ranges: false })
    }

    pub fn size(&self) -> (usize, usize) { (self.width, self.height) }
    pub fn pixels(&self) -> &[u32] { &self.pixels }

    /// Resize presentation storage only. Preserve camera world scale and
    /// center, so exposing more pixels shows more of the same battlefield.
    /// Never allocate a new simulation or touch the authoritative world.
    pub fn resize(&mut self, width: usize, height: usize) -> io::Result<bool> {
        if width == self.width && height == self.height { return Ok(false); }
        if width == 0 || height == 0 || width > 8192 || height > 8192 {
            return Err(io::Error::new(io::ErrorKind::InvalidInput, "invalid viewport size"));
        }
        self.pixels.resize(width * height, 0);
        self.width = width;
        self.height = height;
        self.selection_box = None;
        Ok(true)
    }

    /// Use the authoritative hull and healing-zone colliders for clicks,
    /// retaining screen-space tolerance for small ships. Large hull edges
    /// must remain selectable even when their centers are offscreen.
    /// This read-only UI query never changes gameplay collision state.
    pub fn pick_ship(&self, view: &NativeRenderWorldView<'_>, x: f32, y: f32) -> Option<i64> {
        if !x.is_finite() || !y.is_finite() { return None; }
        let point = self.camera.screen_to_world(x, y, self.width, self.height);
        let radius = 18.0 / self.camera.pixels_per_unit;
        let padding = Vec2::new(radius, radius);
        let bounds = Aabb { min: point - padding, max: point + padding };
        view.combat.physics().input_ship_ids_overlapping_box(bounds).ok()?
            .filter_map(|id| {
                let ship = view.combat.ship(id)?;
                if ship.vitals.is_dead() { return None; }
                let position = view.combat.ship_body(id)?.motion.position;
                let (sx, sy) = self.project(position);
                let distance_squared = (sx - x).powi(2) + (sy - y).powi(2);
                Some((distance_squared, id))
            })
            .min_by(|a, b| a.0.total_cmp(&b.0).then(a.1.cmp(&b.1)))
            .map(|(_, id)| id)
    }

    /// Target selection uses cached authored hull overlap, with a small screen
    /// tolerance. This emits only identities; the host revalidates every action.
    pub fn pick_support_action(&self, view: &NativeRenderWorldView<'_>,
        mode: super::NativeSupportMode, point: Vec2, patrol_start: Option<Vec2>,
    ) -> Option<bees_gameplay::player_inputs::NativePlayerSupportAction> {
        use super::NativeSupportMode as Mode;
        use bees_gameplay::player_inputs::NativePlayerSupportAction as Action;
        if !point.is_finite() { return None; }
        if mode == Mode::Patrol {
            let start = patrol_start.filter(|start| start.is_finite())?;
            return Some(Action::Patrol {
                top_left: Vec2::new(start.x.min(point.x), start.y.max(point.y)),
                bottom_right: Vec2::new(start.x.max(point.x), start.y.min(point.y)),
            });
        }
        let radius = 5.0 / self.camera.pixels_per_unit;
        let bounds = Aabb { min: point - Vec2::new(radius, radius),
            max: point + Vec2::new(radius, radius) };
        if mode == Mode::Mining {
            let id = view.combat.physics().obstacle_ids_overlapping_box(bounds).ok()?
                .filter_map(|id| view.combat.mining_asteroid_position(id)
                    .map(|position| ((position - point).length_squared(), id)))
                .min_by(|a, b| a.0.total_cmp(&b.0).then(a.1.cmp(&b.1)))?.1;
            return Some(Action::Mining { asteroid_id: id });
        }
        let selected_side = self.selection.squads().first()
            .and_then(|owner| view.roster.get(*owner)).map(|squad| squad.side);
        let id = view.combat.physics().input_ship_ids_overlapping_box(bounds).ok()?
            .filter_map(|id| {
                let ship = view.combat.ship(id)?;
                if ship.vitals.is_dead() || selected_side.is_none() ||
                    selected_side.is_some_and(|side| if mode == Mode::Attack { ship.side == side } else { ship.side != side }) ||
                    (mode == Mode::Heal && ship.ship_type != bees_sim::combat_world::BEEHIVE_TYPE) ||
                    (mode == Mode::FullRetreat && ship.ship_type != bees_sim::combat_world::WARP_GATE_TYPE) {
                    return None;
                }
                let position = view.combat.ship_body(id)?.motion.position;
                Some(((position - point).length_squared(), id))
            }).min_by(|a, b| a.0.total_cmp(&b.0).then(a.1.cmp(&b.1)))?.1;
        match mode {
            Mode::Attack => Some(Action::Attack { target: view.roster.owning_squad(id)? }),
            Mode::Guard => Some(Action::Guard { target: view.roster.owning_squad(id)? }),
            // Source input sends every Beehive in the clicked squad, in
            // GetShips order, rather than silently restricting it to one hull.
            Mode::Heal => Some(Action::Heal { beehive_ids: view.roster.get(view.roster.owning_squad(id)?)?
                .members.iter().copied().filter(|id| view.combat.ship(*id).is_some_and(|ship|
                    !ship.vitals.is_dead() && ship.ship_type == bees_sim::combat_world::BEEHIVE_TYPE))
                .collect() }),
            Mode::FullRetreat => Some(Action::FullRetreat { warp_gate_id: id }),
            _ => None,
        }
    }

    pub fn render(&mut self, view: &NativeRenderWorldView<'_>, bounds: Aabb) -> NativeRenderCounts {
        self.selection.synchronize(view);
        // Selected squad count can approach ship count in large battles.
        // Keep 1v1 allocation-free and reuse the large-selection lookup
        // rather than allocating a new hash table every rendered frame.
        let many_selected = self.selection.squads().len() > 8;
        self.selected_squad_lookup.clear();
        if many_selected {
            self.selected_squad_lookup.extend(self.selection.squads().iter().copied());
        }
        // The fixed software viewport is axis-aligned in world space.
        // Margin preserves center markers, health and selected-ship UI at
        // its edges. Cached physical hull bounds stay authoritative.
        let pad = Vec2::new(24.0, 24.0) * (1.0 / self.camera.pixels_per_unit);
        let viewport = Aabb {
            min: self.camera.screen_to_world(
                0.0, self.height as f32, self.width, self.height,
            ) - pad,
            max: self.camera.screen_to_world(
                self.width as f32, 0.0, self.width, self.height,
            ) + pad,
        };
        self.pixels.fill(0x080e19);
        let mut counts = NativeRenderCounts::default();
        self.shape(&ShipShape::Box { offset: (bounds.min + bounds.max) * 0.5,
            half_extents: (bounds.max - bounds.min) * 0.5 },
            Pose { position: Vec2::ZERO, rotation_degrees: 0.0 }, 0x33455e);
        for (_, body) in view.combat.render_obstacles() {
            counts.obstacles += 1;
            if body.primary_bounds().is_some_and(|hull| !hull.overlaps(viewport)) {
                counts.culled_obstacles += 1;
                continue;
            }
            self.shape(body.shape(), body.pose, 0x758496);
        }
        for piece in view.combat.render_asteroid_pieces() {
            counts.pieces += 1;
            let position = piece.pose.position;
            if position.x < viewport.min.x || position.x > viewport.max.x ||
                position.y < viewport.min.y || position.y > viewport.max.y
            {
                counts.culled_pieces += 1;
                continue;
            }
            let c = (piece.sprite_opacity() * 160.0) as u32;
            self.cross(position, 1.0, c * 0x010101);
        }
        for (ship, body) in view.combat.render_ships() {
            counts.ships += 1;
            let selected_ship = view.roster.owning_squad(ship.entity_id).is_some_and(|owner|
                if many_selected { self.selected_squad_lookup.contains(&owner) }
                else { self.selection.squads().contains(&owner) }
            );
            let position = body.motion.position;
            let center_visible = position.x >= viewport.min.x &&
                position.x <= viewport.max.x &&
                position.y >= viewport.min.y &&
                position.y <= viewport.max.y;
            // Selected offscreen ships still draw their movement and target
            // lines, which may cross the camera. Never cull those commands.
            if !selected_ship && !center_visible &&
                body.primary_bounds().is_some_and(|hull| !hull.overlaps(viewport))
            {
                counts.culled_ships += 1;
                continue;
            }
            let color = side_color(ship.side);
            self.shape(body.shape(), Pose { position: body.motion.position,
                rotation_degrees: body.motion.rotation_degrees }, color);
            self.cross(body.motion.position, 1.5, color);
            let (x, y) = self.project(body.motion.position);
            let health = ship.vitals.health() as f32 / ship.vitals.max_health() as f32;
            self.line(x - 10.0, y - 12.0, x + 10.0, y - 12.0, 0x3c2837);
            self.line(x - 10.0, y - 12.0, x - 10.0 + 20.0 * health, y - 12.0, color);
            if selected_ship {
                self.line(x - 14.0, y - 17.0, x + 14.0, y - 17.0, 0xffffff);
                self.line(x - 14.0, y + 14.0, x + 14.0, y + 14.0, 0xffffff);
                for slot in 0..5 {
                    if let Ok(Some(charge)) = view.combat.weapon_charge(ship.entity_id, slot) {
                        let progress = (charge.elapsed_seconds / charge.event_seconds).clamp(0.0, 1.0);
                        let cy = y + 18.0 + slot as f32 * 3.0;
                        self.line(x - 10.0, cy, x + 10.0, cy, 0x34435a);
                        self.line(x - 10.0, cy, x - 10.0 + 20.0 * progress, cy,
                            if charge.paused { 0xe1a34b } else { 0x62b9ff });
                    }
                }
                if self.show_weapon_ranges {
                    for slot in 0..5 {
                        let range = match view.combat.weapon_range(ship.entity_id, slot) {
                            Ok(Some(range)) => range,
                            Ok(None) => continue,
                            Err(_) => { counts.missing_range_data += 1; continue; },
                        };
                        if let Ok(Some(automatic)) = view.combat.weapon_automatic_status(ship.entity_id, slot) {
                            counts.automatic_targets += usize::from(automatic.target_ship.is_some());
                        }
                        if let Ok(Some(contacts)) = view.combat.weapon_range_contacts(ship.entity_id, slot) {
                            counts.range_contacts += contacts.ship_ids.len();
                        }
                        self.shape(&ShipShape::Circle { offset: Vec2::ZERO, radius: range.radius },
                            Pose { position: range.center, rotation_degrees: 0.0 },
                            if range.enabled { 0x355b80 } else { 0x555555 });
                        let Some(has_obstacles) = view.player_authority.weapon_obstacles_enabled()
                            else { counts.missing_range_data += 1; continue; };
                        for (target, _) in view.combat.render_ships() {
                            if target.side == ship.side { continue; }
                            counts.range_queries += 1;
                            match view.combat.query_weapon_target_geometry(ship.entity_id, slot,
                                target.entity_id, view.collision_matrix, has_obstacles) {
                                Ok(Some(geometry)) if geometry.enemy_hull_overlaps_range => {
                                    counts.range_overlaps += 1;
                                    match view.combat.weapon_target_point(ship.entity_id, slot, target.entity_id) {
                                        Ok(point) => { self.cross(point, 2.5, 0xf6df85); counts.aim_points += 1; },
                                        Err(_) => counts.missing_aim_data += 1,
                                    }
                                    counts.obstructed_ranges += usize::from(geometry.line_obstructed);
                                    let (ox, oy) = self.project(range.origin);
                                    let (tx, ty) = self.project(geometry.closest_hull_point);
                                    self.line(ox, oy, tx, ty,
                                        if geometry.line_obstructed { 0xe1a34b } else { 0x62b9ff });
                                },
                                Ok(_) => (),
                                Err(_) => counts.missing_range_data += 1,
                            }
                        }
                    }
                }
                if let Some(destination) = view.combat.ship_game_destination(ship.entity_id) {
                    let (tx, ty) = self.project(destination);
                    self.line(x, y, tx, ty, 0x526a8a);
                    self.cross(destination, 3.0, 0xddeaff);
                }
            }
        }
        for (_, body) in view.combat.render_projectiles() {
            counts.projectiles += 1;
            let position = body.motion.position;
            let center_visible = position.x >= viewport.min.x &&
                position.x <= viewport.max.x &&
                position.y >= viewport.min.y &&
                position.y <= viewport.max.y;
            let outside_hull = match body.shape() {
                None => true,
                Some(_) => body.primary_bounds()
                    .is_some_and(|hull| !hull.overlaps(viewport)),
            };
            // The script-entity center marker is real UI even when its
            // collider is displaced, so require both to be offscreen.
            if !center_visible && outside_hull {
                counts.culled_projectiles += 1;
                continue;
            }
            if let Some(shape) = body.shape() {
                self.shape(shape, Pose { position,
                    rotation_degrees: body.rotation_degrees }, 0xffd580);
            }
            // Collider-less script entities still have an inspectable position.
            self.cross(position, 1.0, 0xffd580);
        }
        if let Some(bounds) = self.selection_box {
            self.shape(&ShipShape::Box { offset: (bounds.min + bounds.max) * 0.5,
                half_extents: (bounds.max - bounds.min) * 0.5 },
                Pose { position: Vec2::ZERO, rotation_degrees: 0.0 }, 0xddeaff);
        }
        counts
    }

    /// Portable capture for diagnostics, using exactly the window's pixel buffer.
    pub fn write_ppm(&self, mut writer: impl Write) -> io::Result<()> {
        write!(writer, "P6\n{} {}\n255\n", self.width, self.height)?;
        let mut row = Vec::with_capacity(self.width * 3);
        for pixels in self.pixels.chunks(self.width) {
            row.clear();
            for &p in pixels { row.extend_from_slice(&[(p >> 16) as u8, (p >> 8) as u8, p as u8]); }
            writer.write_all(&row)?;
        }
        Ok(())
    }

    fn project(&self, point: Vec2) -> (f32, f32) {
        ((point.x - self.camera.center.x) * self.camera.pixels_per_unit + self.width as f32 * 0.5,
         (self.camera.center.y - point.y) * self.camera.pixels_per_unit + self.height as f32 * 0.5)
    }

    fn cross(&mut self, point: Vec2, radius: f32, color: u32) {
        let (x, y) = self.project(point);
        self.line(x - radius, y, x + radius, y, color);
        self.line(x, y - radius, x, y + radius, color);
    }

    fn shape(&mut self, shape: &ShipShape, pose: Pose, color: u32) {
        // Match Pose::transform_point exactly, but compute the rotation
        // once per shape instead of twice per edge. Large authored polygon
        // hulls and weapon-range circles can have dozens of segments.
        let (sin, cos) = pose.rotation_degrees.to_radians().sin_cos();
        let world_point = |point: Vec2| Vec2::new(
            pose.position.x + point.x * cos - point.y * sin,
            pose.position.y + point.x * sin + point.y * cos,
        );
        let mut edge = |a, b| {
            let (x0, y0) = self.project(world_point(a));
            let (x1, y1) = self.project(world_point(b));
            self.line(x0, y0, x1, y1, color);
        };
        match shape {
            ShipShape::Polygon(vertices) => {
                if let Some(&last) = vertices.last() {
                    let mut previous = last;
                    for &point in vertices.iter() { edge(previous, point); previous = point; }
                }
            }
            ShipShape::Box { offset: o, half_extents: h } => {
                let corners = [*o + Vec2::new(-h.x, -h.y), *o + Vec2::new(h.x, -h.y),
                    *o + Vec2::new(h.x, h.y), *o + Vec2::new(-h.x, h.y)];
                for i in 0..4 { edge(corners[i], corners[(i + 1) % 4]); }
            }
            ShipShape::Circle { offset, radius } => {
                // All circles use the identical 32-segment tactical outline.
                // Calculate unit vectors once, not 32 trigonometric calls for
                // every projectile, ship and inspection range in every frame.
                const SEGMENTS: usize = 32;
                static UNIT_CIRCLE: OnceLock<[Vec2; SEGMENTS + 1]> = OnceLock::new();
                let vertices = UNIT_CIRCLE.get_or_init(|| std::array::from_fn(|i| {
                    let (sin, cos) = (std::f32::consts::TAU * i as f32
                        / SEGMENTS as f32).sin_cos();
                    Vec2::new(cos, sin)
                }));
                for pair in vertices.windows(2) {
                    edge(*offset + pair[0] * *radius,
                         *offset + pair[1] * *radius);
                }
            }
        }
    }

    fn line(&mut self, x0: f32, y0: f32, x1: f32, y1: f32, color: u32) {
        // Liang-Barsky clipping bounds raster work even for far-off-map geometry.
        if ![x0, y0, x1, y1].iter().all(|x| x.is_finite()) { return; }
        let dx = x1 - x0;
        let dy = y1 - y0;
        let mut lo = 0.0_f32;
        let mut hi = 1.0_f32;
        for (p, q) in [(-dx, x0), (dx, self.width as f32 - 1.0 - x0),
            (-dy, y0), (dy, self.height as f32 - 1.0 - y0)] {
            if p == 0.0 { if q < 0.0 { return; } }
            else if p < 0.0 { lo = lo.max(q / p); }
            else { hi = hi.min(q / p); }
        }
        if lo > hi { return; }
        let ax = x0 + dx * lo;
        let ay = y0 + dy * lo;
        let bx = x0 + dx * hi;
        let by = y0 + dy * hi;
        let steps = ((bx - ax).abs().max((by - ay).abs()).ceil() as usize).max(1);
        for i in 0..=steps {
            let t = i as f32 / steps as f32;
            let x = (ax + (bx - ax) * t).round() as i32;
            let y = (ay + (by - ay) * t).round() as i32;
            if x >= 0 && y >= 0 && (x as usize) < self.width && (y as usize) < self.height {
                self.pixels[y as usize * self.width + x as usize] = color;
            }
        }
    }
}

fn side_color(side: i32) -> u32 {
    const COLORS: [u32; 6] = [0x62d6ff, 0xff778e, 0x8ee6a1, 0xd3a0ff, 0xffd166, 0xe4ecfa];
    COLORS[side.rem_euclid(COLORS.len() as i32) as usize]
}
