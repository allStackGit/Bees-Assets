//! The single tactical command-strip layout used by rendering and pointer
//! hit-testing. These are UI intents only: all resulting orders still enter
//! the existing player input / command and combat authority paths.

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum HudAction {
    Attack, Guard, Patrol, Mine, Heal,
    Retreat, Hold, CeaseFire, AttackOnSight, PointFire,
    StopFire, Beacon, Detonate, Charge, SelectAll,
    MatchSpeed, FreeSpeed,
}

pub const ACTIONS: [HudAction; 17] = [
    HudAction::Attack, HudAction::Guard, HudAction::Patrol,
    HudAction::Mine, HudAction::Heal,
    HudAction::Retreat, HudAction::Hold, HudAction::CeaseFire,
    HudAction::AttackOnSight, HudAction::PointFire,
    HudAction::StopFire, HudAction::Beacon, HudAction::Detonate,
    HudAction::Charge, HudAction::SelectAll,
    HudAction::MatchSpeed, HudAction::FreeSpeed,
];

impl HudAction {
    pub fn hint(self) -> &'static str {
        match self {
            Self::Attack => "Attack: click an enemy ship",
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
            Self::Charge => "Charge forward with ready Barges (Q)",
            Self::SelectAll => "Select all authorized squads on your current side (Ctrl+A)",
            Self::MatchSpeed => "Match the slowest authored speed of each squad (Y)",
            Self::FreeSpeed => "Restore every selected ship's authored speed (U)",
        }
    }

    pub fn label(self) -> &'static str {
        match self {
            Self::Attack => "ATTACK",
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

impl HudRect {
    pub fn contains(self, x: f32, y: f32) -> bool {
        x.is_finite() && y.is_finite() &&
            x >= self.left as f32 && y >= self.top as f32 &&
            x < self.right as f32 && y < self.bottom as f32
    }
}

/// Keep the strip out of the lower-left minimap and lower-right special
/// readiness panels. Hide rather than crowd the battlefield on small windows.
/// Five columns / three rows scale to any sufficiently wide viewport.
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
    let top = height - 134 + index / COLS * (BUTTON_H + GAP);
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

pub fn hit(width: usize, height: usize, x: f32, y: f32) -> Option<HudAction> {
    ACTIONS.iter().copied().enumerate().find_map(|(index, action)|
        action_rect(width, height, index)
            .filter(|rect| rect.contains(x, y))
            .map(|_| action)
    )
}
