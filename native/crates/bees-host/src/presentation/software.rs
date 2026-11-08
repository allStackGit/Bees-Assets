//! Small, window-independent tactical renderer of authoritative live geometry.
//! Owns only pixels/camera/selection; it never mutates or steps a battle.

use std::io::{self, Write};
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
    pub projectiles: usize,
    pub pieces: usize,
}

pub struct NativeSoftwareRenderer {
    width: usize,
    height: usize,
    pixels: Vec<u32>,
    pub camera: NativeCamera,
    /// Selection is scoped to a WorldId AND episode revision by the client.
    pub selected_ship: Option<i64>,
    pub selection: NativeSquadSelection,
    pub selection_box: Option<Aabb>,
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
            selection: NativeSquadSelection::default(), selection_box: None })
    }

    pub fn size(&self) -> (usize, usize) { (self.width, self.height) }
    pub fn pixels(&self) -> &[u32] { &self.pixels }

    /// Selection has a screen-space tolerance so tiny ships remain pickable.
    /// It affects inspection only, never policy perception or physics queries.
    pub fn pick_ship(&self, view: &NativeRenderWorldView<'_>, x: f32, y: f32) -> Option<i64> {
        view.combat.render_ships().filter_map(|(ship, body)| {
            let (sx, sy) = self.project(body.motion.position);
            let d2 = (sx - x).powi(2) + (sy - y).powi(2);
            (d2 <= 18.0 * 18.0).then_some((d2, ship.entity_id))
        }).min_by(|a, b| a.0.total_cmp(&b.0).then(a.1.cmp(&b.1)))
            .map(|(_, id)| id)
    }

    /// Source right-click prioritizes an enemy's actual input collider,
    /// then the LevelInputManager's 10-world-unit proximity fallback.
    /// An enemy click must never silently become a plain move, even if
    /// the ship has not yet been registered in the native squad roster.
    /// A future attack adapter must resolve this real ship ID to its
    /// authoritative squad lifetime before issuing the actual command.
    pub fn pick_enemy_ship(
        &self, view: &NativeRenderWorldView<'_>, point: Vec2,
    ) -> Option<i64> {
        if !point.is_finite() { return None; }
        let selected_side = self.selection.squads().first()
            .and_then(|owner| view.roster.get(*owner))?.side;
        let inside = Vec2::new(0.05, 0.05);
        let bounds = Aabb { min: point - inside, max: point + inside };
        let overlapping = view.combat.physics()
            .input_ship_ids_overlapping_box(bounds).ok()
            .and_then(|ids| ids.filter_map(|id| {
                let ship = view.combat.ship(id)?;
                let body = view.combat.ship_body(id)?;
                if ship.side == selected_side || ship.vitals.is_dead() ||
                    !body.active
                {
                    return None;
                }
                Some(((body.motion.position - point).length_squared(), id))
            }).min_by(|a, b| a.0.total_cmp(&b.0).then(a.1.cmp(&b.1))));
        if let Some((_, id)) = overlapping { return Some(id); }
        view.combat.render_ships().filter_map(|(ship, body)| {
            if ship.side == selected_side || ship.vitals.is_dead() {
                return None;
            }
            let distance_squared = (body.motion.position - point).length_squared();
            (distance_squared <= 100.0).then_some((distance_squared, ship.entity_id))
        }).min_by(|a, b| a.0.total_cmp(&b.0).then(a.1.cmp(&b.1)))
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
                if ship.vitals.is_dead() || selected_side.is_some_and(|side| ship.side != side) ||
                    (mode == Mode::Heal && ship.ship_type != bees_sim::combat_world::BEEHIVE_TYPE) ||
                    (mode == Mode::FullRetreat && ship.ship_type != bees_sim::combat_world::WARP_GATE_TYPE) {
                    return None;
                }
                let position = view.combat.ship_body(id)?.motion.position;
                Some(((position - point).length_squared(), id))
            }).min_by(|a, b| a.0.total_cmp(&b.0).then(a.1.cmp(&b.1)))?.1;
        match mode {
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
        self.pixels.fill(0x080e19);
        let mut counts = NativeRenderCounts::default();
        self.shape(&ShipShape::Box { offset: (bounds.min + bounds.max) * 0.5,
            half_extents: (bounds.max - bounds.min) * 0.5 },
            Pose { position: Vec2::ZERO, rotation_degrees: 0.0 }, 0x33455e);
        for (_, body) in view.combat.render_obstacles() {
            counts.obstacles += 1;
            self.shape(body.shape(), body.pose, 0x758496);
        }
        for piece in view.combat.render_asteroid_pieces() {
            counts.pieces += 1;
            let c = (piece.sprite_opacity() * 160.0) as u32;
            self.cross(piece.pose.position, 1.0, c * 0x010101);
        }
        for (ship, body) in view.combat.render_ships() {
            counts.ships += 1;
            let color = side_color(ship.side);
            self.shape(body.shape(), Pose { position: body.motion.position,
                rotation_degrees: body.motion.rotation_degrees }, color);
            self.cross(body.motion.position, 1.5, color);
            let (x, y) = self.project(body.motion.position);
            let health = ship.vitals.health() as f32 / ship.vitals.max_health() as f32;
            self.line(x - 10.0, y - 12.0, x + 10.0, y - 12.0, 0x3c2837);
            self.line(x - 10.0, y - 12.0, x - 10.0 + 20.0 * health, y - 12.0, color);
            if view.roster.owning_squad(ship.entity_id)
                .is_some_and(|owner| self.selection.squads().contains(&owner)) {
                self.line(x - 14.0, y - 17.0, x + 14.0, y - 17.0, 0xffffff);
                self.line(x - 14.0, y + 14.0, x + 14.0, y + 14.0, 0xffffff);
                if let Some(destination) = view.combat.ship_game_destination(ship.entity_id) {
                    let (tx, ty) = self.project(destination);
                    self.line(x, y, tx, ty, 0x526a8a);
                    self.cross(destination, 3.0, 0xddeaff);
                }
            }
        }
        for (_, body) in view.combat.render_projectiles() {
            counts.projectiles += 1;
            if let Some(shape) = body.shape() {
                self.shape(shape, Pose { position: body.motion.position,
                    rotation_degrees: body.rotation_degrees }, 0xffd580);
            }
            // Collider-less script entities still have an inspectable position.
            self.cross(body.motion.position, 1.0, 0xffd580);
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
        let mut edge = |a, b| {
            let (x0, y0) = self.project(pose.transform_point(a));
            let (x1, y1) = self.project(pose.transform_point(b));
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
                let mut previous = *offset + Vec2::new(*radius, 0.0);
                for i in 1..=32 {
                    let (sin, cos) = (std::f32::consts::TAU * i as f32 / 32.0).sin_cos();
                    let point = *offset + Vec2::new(cos, sin) * *radius;
                    edge(previous, point); previous = point;
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
