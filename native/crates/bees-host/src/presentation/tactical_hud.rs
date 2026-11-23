//! The single tactical command-strip layout used by rendering and pointer
//! hit-testing. These are UI intents only: all resulting orders still enter
//! the existing player input / command and combat authority paths.

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum HudAction {
    Attack, Guard, Patrol, Mine, Heal,
    Retreat, Hold, CeaseFire, AttackOnSight, PointFire,
    StopFire, Beacon, Detonate, Charge, SelectAll,
    MatchSpeed, FreeSpeed, Shooting, LockOn, Chase,
    Circle, RightSwipe, LeftSwipe, InAndOut,
}

pub(super) const CONTEXT_ATTACK_RADIUS_SQUARED: f32 = 100.0;
pub(super) const DESTINATION_MARKER_RADIUS_PIXELS: i32 = 3;

/// World-anchored version of the source red Alarm Reaction panel and '!'.
pub(super) fn draw_scout_alarm(
    pixels: &mut [u32], stride: usize, field_width: usize, x: i32, y: i32,
) -> bool {
    let width = 10;
    let height = 16;
    let x = x.saturating_add(4);
    let y = y.saturating_sub(height);
    let height_limit = (pixels.len() / stride) as i32;
    if x < 0 || y < 0 || x > field_width as i32 - width || y > height_limit - height {
        return false;
    }
    for row in 0..height {
        let start = (y + row) as usize * stride + x as usize;
        pixels[start..start + width as usize].fill(0xb03737);
        if (3..9).contains(&row) || (11..13).contains(&row) {
            pixels[start + 4..start + 6].fill(0xffffff);
        }
    }
    true
}

/// A Scout's vertical ChargingBar fills in twenty half-second steps.
pub(super) fn draw_scout_beacon_charge(
    pixels: &mut [u32], stride: usize, field_width: usize,
    center: (i32, i32), percent: u8,
) -> bool {
    const STEPS: i32 = 20;
    const WIDTH: usize = 4;
    let left = center.0.saturating_add(13);
    let top = center.1.saturating_sub(8);
    if stride == 0 || field_width > stride || left < 0 || top < 0 ||
        left as usize + WIDTH > field_width ||
        top as usize + STEPS as usize + 2 > pixels.len() / stride
    {
        return false;
    }

    let filled = i32::from(percent.min(100)) * STEPS / 100;
    for y in 0..STEPS + 2 {
        let start = (top + y) as usize * stride + left as usize;
        let row = &mut pixels[start..start + WIDTH];
        row.fill(0xe8edf3);
        if y > 0 && y <= STEPS {
            row[1..WIDTH - 1].fill(if y > STEPS - filled { 0x39db3f } else { 0x253747 });
        }
    }
    true
}

pub(super) fn battle_marker_color(
    custom_rgb: Option<[u8; 3]>, side: i32, human_side: Option<i32>, fallback: u32,
) -> u32 {
    if let Some([red, green, blue]) = custom_rgb {
        return u32::from_be_bytes([0, red, green, blue]);
    }
    match human_side {
        Some(human) if side == human => 0x277f5e,
        Some(human) if matches!(human, 1 | 2) && matches!(side, 1 | 2) => 0xfbf236,
        _ => fallback,
    }
}

pub const ACTIONS: [HudAction; 24] = [
    HudAction::Attack, HudAction::Guard, HudAction::Patrol,
    HudAction::Mine, HudAction::Heal,
    HudAction::Retreat, HudAction::Hold, HudAction::CeaseFire,
    HudAction::AttackOnSight, HudAction::PointFire,
    HudAction::StopFire, HudAction::Beacon, HudAction::Detonate,
    HudAction::Charge, HudAction::SelectAll,
    HudAction::MatchSpeed, HudAction::FreeSpeed, HudAction::Shooting,
    HudAction::LockOn, HudAction::Chase,
    HudAction::Circle, HudAction::RightSwipe,
    HudAction::LeftSwipe, HudAction::InAndOut,
];

impl HudAction {
    pub fn hint(self) -> &'static str {
        match self {
            Self::Attack => "Attack: click an enemy ship",
            Self::Circle => "Circle: click the enemy squad to orbit",
            Self::RightSwipe => "Right Swipe: click enemy to make a flanking pass",
            Self::LeftSwipe => "Left Swipe: click enemy to make a flanking pass",
            Self::InAndOut => "In and Out: click enemy for a strike-and-withdraw approach",
            Self::Guard => "Guard: click a friendly ship",
            Self::Patrol => "Patrol: drag a rectangular patrol area",
            Self::Mine => "Mine: click a mining asteroid",
            Self::Heal => "Heal: click a friendly Beehive",
            Self::Retreat => "Retreat: click a WarpGate",
            Self::Hold => "Hold: cancel an active chase (C)",
            Self::CeaseFire => "Cease fire: selected squad weapons off (V)",
            Self::AttackOnSight => "Attack on sight: selected squad weapons on (A)",
            Self::PointFire => "Point fire: click a location, X stops (T)",
            Self::StopFire => "Stop manually targeting (X)",
            Self::Beacon => "Deploy a ready Scout Beacon (K)",
            Self::Detonate => "Detonate selected Fire Barges (D)",
            Self::Charge => "Charge forward or queue the next Barge charge (Q)",
            Self::SelectAll => "Select all authorized squads on your current side (Ctrl+A)",
            Self::MatchSpeed => "Match the slowest authored speed of each squad (Y)",
            Self::FreeSpeed => "Restore every selected ship's authored speed (U)",
            Self::Shooting => "Choose any of the 40 native shooting priorities (S)",
            Self::LockOn => "Lock/unlock enemy commands; locked squads ignore ground moves (O)",
            Self::Chase => "Enable automatic nearest-visible-enemy pursuit (E); Hold stops it",
        }
    }

    pub fn label(self) -> &'static str {
        match self {
            Self::Attack => "ATTACK",
            Self::Circle => "CIRCLE",
            Self::RightSwipe => "SWIPE R",
            Self::LeftSwipe => "SWIPE L",
            Self::InAndOut => "IN & OUT",
            Self::Guard => "GUARD",
            Self::Patrol => "PATROL",
            Self::Mine => "MINE",
            Self::Heal => "HEAL",
            Self::Retreat => "RETREAT",
            Self::Hold => "HOLD",
            Self::CeaseFire => "CEASE",
            Self::AttackOnSight => "SIGHT",
            Self::PointFire => "POINT",
            Self::StopFire => "STOP FIRE",
            Self::Beacon => "BEACON",
            Self::Detonate => "DETONATE",
            Self::Charge => "CHARGE",
            Self::SelectAll => "ALL",
            Self::MatchSpeed => "MATCH",
            Self::FreeSpeed => "UNMATCH",
            Self::Shooting => "TARGETS",
            Self::LockOn => "LOCK ON",
            Self::Chase => "CHASE",
        }
    }
}

#[derive(Clone, Copy)]
pub struct HudRect {
    pub left: usize,
    pub top: usize,
    pub right: usize,
    pub bottom: usize,
}

/// Mark the actual clickable HUD bounds without changing hit testing.
pub(super) fn draw_tutorial_highlight(pixels: &mut [u32], stride: usize, rect: HudRect) {
    if stride == 0 || rect.left >= rect.right || rect.top >= rect.bottom ||
        rect.right > stride || rect.bottom > pixels.len() / stride
    {
        return;
    }
    const COLOR: u32 = 0xdce25f;
    for y in [rect.top, rect.bottom - 1] {
        let start = y * stride;
        pixels[start + rect.left..start + rect.right].fill(COLOR);
    }
    for y in rect.top + 1..rect.bottom - 1 {
        let start = y * stride;
        pixels[start + rect.left] = COLOR;
        pixels[start + rect.right - 1] = COLOR;
    }
}

impl HudRect {
    pub fn contains(self, x: f32, y: f32) -> bool {
        x.is_finite() && y.is_finite() &&
            x >= self.left as f32 && y >= self.top as f32 &&
            x < self.right as f32 && y < self.bottom as f32
    }
}

/// Source Pluto4BluerPastures updates PlutoShieldHealthBar in 200-HP
/// civilian-loss increments, each worth one fifteenth of its 150-pixel
/// meter. The actual protected ship is read in the owning local/remote
/// World, so this UI never reconstructs a separate mission health value.
/// The HUD coordinates are shared by local and LAN presentation.
pub(super) fn draw_pluto_shield_bar(
    pixels: &mut [u32], stride: usize, field_width: usize,
    field_height: usize, health: i32, max_health: i32,
) -> bool {
    const WIDTH: usize = 150;
    const HEIGHT: usize = 9;
    const TOP: usize = 51;
    if field_width < 230 || field_height < TOP + HEIGHT + 6 ||
        stride < field_width || max_health <= 0 ||
        pixels.len() < stride.saturating_mul(field_height) {
        return false;
    }
    let lost = (max_health.saturating_sub(health).max(0) / 200).min(15);
    let fill = WIDTH * (15 - lost) as usize / 15;
    let left = (field_width - WIDTH) / 2;
    for y in TOP..TOP + HEIGHT {
        let row = &mut pixels[y * stride + left..y * stride + left + WIDTH];
        row.fill(0x1a2639);
        if y > TOP && y + 1 < TOP + HEIGHT {
            row[..fill].fill(if lost >= 10 { 0xd87b71 } else if lost >= 5 {
                0xe9c579
            } else { 0x79d7b0 });
        }
    }
    true
}

/// Source Neptune II and Uranus II have a campaign-level Retreat
/// button distinct from the squad's WarpGate "RETREAT" command. Keep one
/// shared hit/paint rectangle for local and authenticated LAN clients;
/// never interpret a click as a command in a spectator or inactive World.
pub fn retreat_button_rect(width: usize, height: usize) -> Option<HudRect> {
    if width < 760 || height < 420 { return None; }
    let right = width.saturating_sub(20);
    let left = right.saturating_sub(210);
    let top = 54;
    Some(HudRect { left, top, right, bottom: top + 27 })
}

/// Keep the strip out of the lower-left minimap and lower-right special
/// readiness panels. Hide rather than crowd the battlefield on small windows.
/// Five columns / five rows scale to any sufficiently wide viewport.
pub fn action_rect(width: usize, height: usize, index: usize) -> Option<HudRect> {
    if width < 980 || height < 430 || index >= ACTIONS.len() {
        return None;
    }
    const COLS: usize = 5;
    const BUTTON_W: usize = 78;
    const BUTTON_H: usize = 25;
    const GAP: usize = 5;
    let panel_width = COLS * BUTTON_W + (COLS - 1) * GAP;
    let left = (width - panel_width) / 2 + index % COLS * (BUTTON_W + GAP);
    let top = height - 164 + index / COLS * (BUTTON_H + GAP);
    Some(HudRect {
        left, top, right: left + BUTTON_W, bottom: top + BUTTON_H,
    })
}

/// One source-ordered squad-tab coordinate contract for drawing and
/// click targets. The tab's text length is measured with the same glyph
/// advances as the renderer; no second cached roster lives in the UI.
pub fn squad_tab_rect(width: usize, height: usize,
    row: usize, text_height: usize, label_width: usize,
) -> Option<HudRect> {
    if width < 560 || height < 360 { return None; }
    let y = 14 + row * (text_height + 6);
    if y + text_height + 5 >= height / 2 { return None; }
    let x = 14usize;
    let box_width = (label_width + 16).min(228);
    Some(HudRect {
        left: x - 5, top: y - 2,
        right: (x - 5 + box_width).min(width),
        bottom: (y + text_height + 4).min(height),
    })
}

/// The full source registry (16 general + 24 ship-type priorities) is
/// offered without guessing which targets happen to be present. Use one
/// pure coordinate contract for display and click handling; never cache
/// button identities or duplicate registry indexes in client state.
pub fn shooting_rect(width: usize, height: usize, index: usize) -> Option<HudRect> {
    if width < 980 || height < 520 || index >= 40 { return None; }
    const COLS: usize = 4;
    const ROWS: usize = 10;
    const W: usize = 170;
    const H: usize = 24;
    const GAP_X: usize = 7;
    const GAP_Y: usize = 5;
    let panel_width = COLS * W + (COLS - 1) * GAP_X;
    let panel_height = ROWS * H + (ROWS - 1) * GAP_Y;
    let left = (width - panel_width) / 2 + index / ROWS * (W + GAP_X);
    let top = (height - panel_height) / 2 + index % ROWS * (H + GAP_Y);
    Some(HudRect {
        left, top, right: left + W, bottom: top + H,
    })
}

pub fn shooting_hit(width: usize, height: usize, x: f32, y: f32) -> Option<u8> {
    (0..40).find(|&index| shooting_rect(width, height, index)
        .is_some_and(|rect| rect.contains(x, y)))
        .map(|index| (index + 1) as u8)
}

pub fn hit(width: usize, height: usize, x: f32, y: f32) -> Option<HudAction> {
    ACTIONS.iter().copied().enumerate().find_map(|(index, action)|
        action_rect(width, height, index)
            .filter(|rect| rect.contains(x, y))
            .map(|_| action)
    )
}
