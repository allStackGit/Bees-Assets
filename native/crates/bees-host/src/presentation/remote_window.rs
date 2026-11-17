//! Native remote battle window using the same read-only authoritative wire
//! projection as the terminal client. No gameplay/physics or model state lives
//! here. All orders use the host's existing NativePlayerInput serializer and
//! player-side permissions; the host validates them again at tick boundaries.

use std::collections::{HashSet, VecDeque};
use std::error::Error;
use std::fs;
use std::io::{self, ErrorKind, Read, Write};
use std::net::{TcpStream, ToSocketAddrs};
use std::path::Path;
use std::time::{Duration, Instant};

use bees_gameplay::campaign_dialogue::CampaignCue;
use bees_sim::collision::Aabb;
use bees_sim::geometry::Vec2;
use minifb::{Key, KeyRepeat, MouseButton, MouseMode, Window, WindowOptions};
use serde_json::{json, Value};

use super::{NativeCamera, text};

const WIDTH: usize = 1180;
const HEIGHT: usize = 720;
const FIELD: usize = 914;
const MAX_INCOMING: usize = 4 * 1024 * 1024;
const MAX_OUTGOING: usize = 128 * 1024;
const READ_BUDGET: usize = 256 * 1024;
const TEXT: [u8; 3] = [231, 240, 250];
const MUTED: [u8; 3] = [161, 181, 201];
const GREEN: u32 = 0x69d2a1;
const RED: u32 = 0xed7e85;
const GOLD: u32 = 0xe7be69;

#[derive(Clone, Copy, PartialEq, Eq)]
enum TargetMode { Fire, Attack, Guard, Mine, Heal, Retreat, Patrol }

#[derive(Default)]
struct WireCounts {
    bytes_received: u64,
    snapshots: u64,
    orders_sent: u64,
    acknowledgments: u64,
}

struct PeerClient {
    socket: TcpStream,
    incoming: Vec<u8>,
    queued: VecDeque<Vec<u8>>,
    written: usize,
    queued_bytes: usize,
    next_request: u64,
    authenticated: bool,
    connected: bool,
    side: i32,
    state: Option<Value>,
    notice: String,
    held_fire_begin_request: Option<u64>,
    held_fire_rejected: bool,
    metrics: WireCounts,
}

fn invalid(message: &'static str) -> io::Error {
    io::Error::new(ErrorKind::InvalidInput, message)
}

impl PeerClient {
    fn connect(address: &str, token: &str, side: i32) -> Result<Self, Box<dyn Error>> {
        let mut connection = None;
        for candidate in address.to_socket_addrs()? {
            match TcpStream::connect_timeout(&candidate, Duration::from_secs(5)) {
                Ok(socket) => { connection = Some(socket); break; }
                Err(_) => continue,
            }
        }
        let mut socket = connection.ok_or_else(|| invalid("native multiplayer server unreachable"))?;
        socket.set_nodelay(true)?;
        let mut hello = serde_json::to_vec(&json!({"kind": "hello", "token": token}))?;
        hello.push(b'\n');
        socket.write_all(&hello)?;
        socket.set_nonblocking(true)?;
        Ok(Self {
            socket, incoming: Vec::new(), queued: VecDeque::new(),
            written: 0, queued_bytes: 0, next_request: 1,
            authenticated: false, connected: true, side,
            state: None, notice: "Authenticating with native host".to_owned(),
            held_fire_begin_request: None, held_fire_rejected: false,
            metrics: WireCounts::default(),
        })
    }

    fn poll(&mut self) -> Result<(), Box<dyn Error>> {
        let mut buffer = [0_u8; 32768];
        let mut read = 0_usize;
        while read < READ_BUDGET && self.connected {
            let limit = buffer.len().min(READ_BUDGET - read);
            match self.socket.read(&mut buffer[..limit]) {
                Ok(0) => { self.connected = false; self.notice = "Host disconnected".into(); break; }
                Ok(n) => {
                    read += n;
                    self.metrics.bytes_received += n as u64;
                    if self.incoming.len() + n > MAX_INCOMING {
                        return Err(invalid("remote world snapshot exceeds client memory limit").into());
                    }
                    self.incoming.extend_from_slice(&buffer[..n]);
                }
                Err(error) if error.kind() == ErrorKind::WouldBlock => break,
                Err(error) if error.kind() == ErrorKind::Interrupted => continue,
                Err(error) => return Err(error.into()),
            }
        }
        // Coalescing is already performed by the authoritative host. Process
        // only complete JSON lines; never render or apply half a message.
        let mut start = 0;
        for index in 0..self.incoming.len() {
            if self.incoming[index] != b'\n' { continue; }
            let message: Value = serde_json::from_slice(&self.incoming[start..index])?;
            start = index + 1;
            match message["kind"].as_str() {
                Some("welcome") => {
                    if message["protocol"].as_u64() != Some(1) ||
                        message["side"].as_i64() != Some(i64::from(self.side)) {
                        return Err(invalid("native host authenticated the wrong player side or protocol").into());
                    }
                    self.authenticated = true;
                    self.notice = format!("Connected as player side {}", self.side);
                }
                Some("world_state") => {
                    if !self.authenticated || message["protocol"].as_u64() != Some(1) ||
                        message["viewer_side"].as_i64() != Some(i64::from(self.side)) {
                        return Err(invalid("native host snapshot has the wrong player side").into());
                    }
                    if let Some(previous) = &self.state {
                        if previous["world_id"] == message["world_id"] &&
                            previous["episode_revision"] == message["episode_revision"] &&
                            message["tick"].as_u64() < previous["tick"].as_u64() {
                            return Err(invalid("out-of-order authoritative world snapshot").into());
                        }
                    }
                    self.state = Some(message);
                    self.metrics.snapshots += 1;
                }
                Some("held_fire_released") => {
                    if message["reason"] != "no_live_weapons" {
                        return Err(invalid("unknown authoritative held-fire release reason").into());
                    }
                    self.held_fire_begin_request = None;
                    self.held_fire_rejected = true;
                    self.notice = "Manual fire ended: no surviving held weapons".into();
                }
                Some("input_result") => {
                    self.metrics.acknowledgments += 1;
                    let request = message["request_id"].as_u64().unwrap_or(0);
                    let feedback = message["feedback"].as_str()
                        .unwrap_or("host acknowledgment");
                    if self.held_fire_begin_request == Some(request) {
                        self.held_fire_begin_request = None;
                        // A rejected Begin must not cause 30 new attempts
                        // per second while the player holds J. Require one
                        // release before requesting a different gesture.
                        if message["processed"] != true ||
                            feedback == "No selected weapons accepted held fire" {
                            self.held_fire_rejected = true;
                        }
                    }
                    self.notice = format!("Order {request}: {feedback}");
                }
                _ => return Err(invalid("unexpected native multiplayer protocol message").into()),
            }
        }
        if start > 0 { self.incoming.drain(..start); }
        self.flush()?;
        Ok(())
    }

    fn send_action(&mut self, action: Value, notice: String) -> Result<(), Box<dyn Error>> {
        if !self.authenticated || !self.connected {
            return Err(invalid("not connected to native host").into());
        }
        let next = self.next_request.checked_add(1)
            .ok_or_else(|| invalid("native multiplayer request ID exhausted"))?;
        let mut packet = serde_json::to_vec(&json!({
            "kind": "input", "request_id": self.next_request, "action": action,
        }))?;
        packet.push(b'\n');
        if self.queued_bytes + packet.len() > MAX_OUTGOING {
            return Err(invalid("outgoing native orders exceed pending network budget").into());
        }
        self.queued_bytes += packet.len();
        self.queued.push_back(packet);
        self.next_request = next;
        self.metrics.orders_sent += 1;
        self.notice = notice;
        self.flush()?;
        Ok(())
    }

    fn selected_owners(state: &Value, selected: &[i64]) -> Vec<Value> {
        state["squads"].as_array().into_iter().flatten()
            .filter(|squad| squad["owned"] == true &&
                squad["controllable"] == true &&
                selected.contains(&squad["owner"]["squad_id"].as_i64().unwrap_or(-1)))
            .map(|squad| squad["owner"].clone()).collect()
    }

    fn order(&mut self, selected: &[i64], kind: &str, fields: Value) -> Result<(), Box<dyn Error>> {
        let state = self.state.as_ref().ok_or_else(||
            invalid("native battle has no snapshot"))?;
        let owners = Self::selected_owners(state, selected);
        if owners.is_empty() {
            return Err(invalid("select a live controllable friendly squad").into());
        }
        let count = owners.len();
        let mut input = json!({
            "world_id": state["world_id"],
            "episode_revision": state["episode_revision"],
            "squads": owners,
        });
        let object = fields.as_object().ok_or_else(|| invalid("invalid native action fields"))?;
        for (key, value) in object { input[key] = value.clone(); }
        self.send_action(json!({"kind": kind, "input": input}),
            format!("Sent {kind} for {count} selected squads"))
    }

    /// Only the Begin packet contains the original squad lifetimes.
    /// The server subsequently owns that gesture until End or disconnect;
    /// Aim never retransmits a new selection into the shared CombatWorld.
    fn held_fire(&mut self, selected: &[i64], event: &str, target: Option<Vec2>)
        -> Result<(), Box<dyn Error>>
    {
        let state = self.state.as_ref().ok_or_else(||
            invalid("native battle has no snapshot"))?;
        let action = match event {
            "Begin" => {
                let owners = Self::selected_owners(state, selected);
                if owners.is_empty() {
                    return Err(invalid("select a controllable squad to begin held fire").into());
                }
                let point = target.ok_or_else(|| invalid("held fire requires a target"))?;
                json!({"Begin": {"squads": owners, "target": {"x": point.x, "y": point.y}}})
            },
            "Aim" => {
                let point = target.ok_or_else(|| invalid("held fire requires a target"))?;
                json!({"Aim": {"target": {"x": point.x, "y": point.y}}})
            },
            "End" => json!("End"),
            _ => return Err(invalid("unknown held-fire gesture").into()),
        };
        let input = json!({
            "world_id": state["world_id"],
            "episode_revision": state["episode_revision"],
            "action": action,
        });
        let begin_request = (event == "Begin").then_some(self.next_request);
        self.send_action(json!({"kind": "held_fire", "input": input}),
            format!("Manual fire: {event}"))?;
        if let Some(request) = begin_request {
            self.held_fire_begin_request = Some(request);
        }
        Ok(())
    }

    fn flush(&mut self) -> io::Result<()> {
        let mut budget = 64 * 1024;
        while budget > 0 && self.connected {
            let Some(front) = self.queued.front() else { break };
            let end = (self.written + budget).min(front.len());
            match self.socket.write(&front[self.written..end]) {
                Ok(0) => { self.connected = false; self.notice = "Host disconnected".into(); break; }
                Ok(n) => {
                    budget -= n;
                    self.written += n;
                    if self.written == front.len() {
                        self.queued_bytes -= front.len();
                        self.queued.pop_front();
                        self.written = 0;
                    }
                }
                Err(error) if error.kind() == ErrorKind::WouldBlock => break,
                Err(error) if error.kind() == ErrorKind::Interrupted => continue,
                Err(error) => return Err(error),
            }
        }
        Ok(())
    }
}

fn position(value: &Value) -> Option<Vec2> {
    let x = value["x"].as_f64()? as f32;
    let y = value["y"].as_f64()? as f32;
    (x.is_finite() && y.is_finite()).then_some(Vec2::new(x, y))
}

fn bounds(state: &Value) -> Option<Aabb> {
    let points = state["map_bounds"].as_array()?;
    if points.len() != 2 { return None; }
    let point = |p: &Value| -> Option<Vec2> {
        let coordinates = p.as_array()?;
        if coordinates.len() != 2 { return None; }
        let x = coordinates[0].as_f64()? as f32;
        let y = coordinates[1].as_f64()? as f32;
        (x.is_finite() && y.is_finite()).then_some(Vec2::new(x, y))
    };
    let min = point(&points[0])?;
    let max = point(&points[1])?;
    (min.x < max.x && min.y < max.y).then_some(Aabb {min, max})
}

fn screen(camera: NativeCamera, point: Vec2) -> (i32, i32) {
    let x = FIELD as f32 * 0.5 + (point.x - camera.center.x) * camera.pixels_per_unit;
    let y = HEIGHT as f32 * 0.5 - (point.y - camera.center.y) * camera.pixels_per_unit;
    // Keep hostile or corrupted positional data from causing unbounded draw
    // loops before the actual pixel plotter clips the viewport.
    (x.clamp(-2000.0, 2000.0) as i32, y.clamp(-2000.0, 2000.0) as i32)
}

fn plot(pixels: &mut [u32], x: i32, y: i32, color: u32) {
    if x >= 0 && x < FIELD as i32 && y >= 0 && y < HEIGHT as i32 {
        pixels[y as usize * WIDTH + x as usize] = color;
    }
}
fn line(pixels: &mut [u32], from: (i32,i32), to: (i32,i32), color: u32) {
    let (mut x, mut y) = from;
    let (end_x, end_y) = to;
    let dx = (end_x - x).abs();
    let dy = -(end_y - y).abs();
    let step_x = if x < end_x {1} else {-1};
    let step_y = if y < end_y {1} else {-1};
    let mut error = dx + dy;
    for _ in 0..8002 {
        plot(pixels, x, y, color);
        if x == end_x && y == end_y { break; }
        let doubled = error * 2;
        if doubled >= dy { error += dy; x += step_x; }
        if doubled <= dx { error += dx; y += step_y; }
    }
}
fn circle(pixels: &mut [u32], (x,y): (i32,i32), radius: i32, color: u32) {
    for dy in -radius..=radius {
        for dx in -radius..=radius {
            if dx*dx + dy*dy <= radius*radius { plot(pixels,x+dx,y+dy,color); }
        }
    }
}
fn outline(pixels: &mut [u32], x: i32,y: i32, w: i32,h: i32,color: u32) {
    line(pixels,(x,y),(x+w,y),color);
    line(pixels,(x+w,y),(x+w,y+h),color);
    line(pixels,(x+w,y+h),(x,y+h),color);
    line(pixels,(x,y+h),(x,y),color);
}

fn first_selected<'a>(state: Option<&'a Value>, selected: &[i64]) -> Option<&'a Value> {
    state?.get("squads")?.as_array()?.iter().find(|squad|
        squad["owned"] == true && squad["owner"]["squad_id"]
            .as_i64().is_some_and(|id| selected.contains(&id)))
}

fn squad_for_ship<'a>(state: &'a Value, id: i64, side: i64) -> Option<&'a Value> {
    state["squads"].as_array()?.iter().find(|squad|
        squad["side"].as_i64() == Some(side) &&
        squad["members"].as_array().is_some_and(|members|
            members.iter().any(|member| member.as_i64() == Some(id))))
}
fn hit<'a>(state: &'a Value, camera: NativeCamera, x:i32,y:i32)
    -> Option<(&'static str, &'a Value)> {
    let mut choice = None;
    let mut best = 18 * 18;
    for kind in ["ships", "obstacles"] {
        for item in state[kind].as_array().into_iter().flatten() {
            let Some(point) = position(item) else { continue };
            let (sx,sy)=screen(camera, point);
            let distance = (sx-x).pow(2)+(sy-y).pow(2);
            if distance < best {
                choice=Some((if kind == "ships" {"ship"} else {"obstacle"}, item));
                best = distance;
            }
        }
    }
    choice
}


fn source_numbered_squads(state: &Value) -> Vec<(u64, i64)> {
    let mut numbered: Vec<_> = state["squads"].as_array().into_iter().flatten()
        .filter(|squad| squad["owned"]==true)
        .filter_map(|squad| Some((
            squad["source_number"].as_u64()?,
            squad["owner"]["squad_id"].as_i64()?,
        ))).collect();
    numbered.sort_unstable_by_key(|&(number, _)| number);
    numbered
}

/// Unity numbered shortcuts wrap against the authored original side count,
/// rather than renumbering after squad casualties.
fn numbered_squad(state: &Value, key: u64) -> Option<i64> {
    let first=state["squads"].as_array()?.iter()
        .find(|s| s["owned"]==true && s["source_number"].as_u64().is_some())?;
    let count=first["original_squad_count"].as_u64()?;
    if count==0 {return None;}
    let input=if key==0 {10} else {key};
    let target=(input-1)%count+1;
    source_numbered_squads(state).into_iter()
        .find(|&(number,_)|number==target).map(|(_,id)|id)
}

fn next_numbered_squad(state: &Value, selected: &[i64]) -> Option<i64> {
    let numbered=source_numbered_squads(state);
    if numbered.is_empty(){return None;}
    let current=selected.first().and_then(|id|
        numbered.iter().position(|&(_,candidate)|candidate==*id));
    Some(numbered[current.map_or(0,|index|(index+1)%numbered.len())].1)
}

fn focus_squad(state: &Value, squad_id: i64) -> Option<Vec2> {
    let squad=state["squads"].as_array()?.iter().find(|s|
        s["owned"]==true && s["owner"]["squad_id"].as_i64()==Some(squad_id))?;
    let ship_id=squad["members"].as_array()?.iter().find_map(Value::as_i64)?;
    state["ships"].as_array()?.iter().find(|ship|
        ship["id"].as_i64()==Some(ship_id)).and_then(position)
}

/// The remote window presents the host's authored dialogue, never a second
/// mission progression state. Peers choose which line to read locally.
fn draw_campaign_dialogue(pixels: &mut [u32], state: &Value, line_index: usize) {
    let Ok(cue)=serde_json::from_value::<CampaignCue>(
        state["campaign_dialogue"]["cue"].clone(),
    ) else {return;};
    let lines=super::campaign_dialogue::lines(cue);
    let Some(&(speaker,message))=lines.get(line_index) else{return;};
    for scanline in pixels.chunks_exact_mut(WIDTH).skip(HEIGHT-160).take(96) {
        scanline[..FIELD].fill(0x1c3045);
    }
    let heading=format!("{}   Dialogue {}/{}",
        speaker,line_index+1,lines.len());
    text::draw_at(pixels,WIDTH,15,HEIGHT-152,
        text::fit(&heading,FIELD-30),1,[251,217,146]);
    let mut rest=message;
    for index in 0..3 {
        if rest.is_empty(){break;}
        let fit=text::fit(rest,FIELD-36);
        let piece=if fit.len()<rest.len(){
            fit.rsplit_once(' ').map_or(fit,|(head,_)|head)
        }else{fit};
        let piece=if piece.is_empty(){fit}else{piece};
        text::draw_at(pixels,WIDTH,15,HEIGHT-127+index*19,piece,1,TEXT);
        rest=rest[piece.len()..].trim_start();
    }
    text::draw_at(pixels,WIDTH,15,HEIGHT-72,
        "Enter next   Backspace previous",1,MUTED);
}

fn draw(pixels: &mut [u32], state: Option<&Value>, camera: Option<NativeCamera>,
    selected: &[i64], notice: &str, side: i32, mode: Option<TargetMode>,
    patrol_start: Option<Vec2>, metrics: &WireCounts, dialogue_line: usize,
    held_fire_active: bool) {
    for scanline in pixels.chunks_exact_mut(WIDTH) {
        scanline[..FIELD].fill(0x080f1a);
        scanline[FIELD..].fill(0x152439);
    }
    text::draw_at(pixels, WIDTH, FIELD + 14, 18, "BEES NATIVE", 2, TEXT);
    text::draw_at(pixels, WIDTH, FIELD + 14, 57,
        &format!("Remote player side {side}"), 1, MUTED);
    if let (Some(state), Some(camera)) = (state,camera) {
        if let Some(map) = bounds(state) {
            let a=screen(camera,map.min);
            let b=screen(camera,map.max);
            outline(pixels,a.0,b.1,b.0-a.0,a.1-b.1,0x395772);
        }
        if let Some(corner)=patrol_start {
            outline(pixels,screen(camera,corner).0-7,
                screen(camera,corner).1-7,14,14,0xf7d68f);
        }
        for obj in state["obstacles"].as_array().into_iter().flatten() {
            if let Some(point)=position(obj) {
                let coord=screen(camera,point);
                circle(pixels,coord,5,if obj["mining"]==true {GOLD} else {0x7d92aa});
            }
        }
        for shot in state["projectiles"].as_array().into_iter().flatten() {
            if let Some(point)=position(shot) { circle(pixels,screen(camera,point),2,0xf8d798); }
        }
        // Build selected ship membership once per rendered snapshot.
        // Avoid an O(ships * squads * members) roster scan on large battles.
        let highlighted: HashSet<i64> = state["squads"].as_array().into_iter().flatten()
            .filter(|s|s["owned"]==true &&
                s["owner"]["squad_id"].as_i64().is_some_and(|id|selected.contains(&id)))
            .flat_map(|s|s["members"].as_array().into_iter().flatten())
            .filter_map(Value::as_i64).collect();
        for ship in state["ships"].as_array().into_iter().flatten() {
            let Some(point)=position(ship) else { continue };
            let center=screen(camera,point);
            // Side allegiance and command rights are separate. The host
            // includes AI-controlled friendly ships but never permits a
            // remote peer to select their unowned squad.
            let friendly=ship["friendly"]==true;
            let color=if friendly {GREEN} else {RED};
            let angle=(ship["rotation"].as_f64().unwrap_or(0.0) as f32).to_radians();
            let (sin,cos)=angle.sin_cos();
            let tip=(center.0+(cos*12.0) as i32,center.1-(sin*12.0) as i32);
            let left=(center.0+((-cos*7.0-sin*7.0) as i32),
                      center.1-((-sin*7.0+cos*7.0) as i32));
            let right=(center.0+((-cos*7.0+sin*7.0) as i32),
                       center.1-((-sin*7.0-cos*7.0) as i32));
            line(pixels,tip,left,color);line(pixels,left,right,color);line(pixels,right,tip,color);
            let health=ship["health"].as_f64().unwrap_or(0.0);
            let maximum=ship["max_health"].as_f64().unwrap_or(1.0).max(1.0);
            if health.is_finite() && maximum.is_finite() {
                line(pixels,(center.0-10,center.1+14),(center.0+10,center.1+14),0x4f6377);
                line(pixels,(center.0-10,center.1+14),
                     (center.0-10+(20.0*(health/maximum).clamp(0.0,1.0)) as i32,center.1+14),
                     color);
            }
            if ship["owned"]==true && highlighted.contains(&ship["id"].as_i64().unwrap_or(-1)) {
                outline(pixels,center.0-15,center.1-17,30,34,0xf9f0a1);
                if let Some(destination)=ship["destination"].as_array() {
                    if destination.len()==2 {
                        if let (Some(x),Some(y))=(destination[0].as_f64(),destination[1].as_f64()) {
                            let dest=Vec2::new(x as f32,y as f32);
                            if dest.is_finite() {line(pixels,center,screen(camera,dest),0x2d6070);}
                        }
                    }
                }
            }
        }
        text::draw_at(pixels, WIDTH, FIELD+14, 90,
            &format!("World {}  Tick {}",state["world_id"],state["tick"]),1,TEXT);
        let friendly=state["squads"].as_array().into_iter().flatten().filter(|s|s["owned"]==true).count();
        text::draw_at(pixels, WIDTH, FIELD+14, 111,
            &format!("Squads {friendly}   Selected {}",selected.len()),1,MUTED);
        for (index,squad) in state["squads"].as_array().into_iter().flatten()
            .filter(|s|s["owned"]==true).take(12).enumerate() {
            let id=squad["owner"]["squad_id"].as_i64().unwrap_or(-1);
            let label=format!("{} ID {}: {} ships",if selected.contains(&id) {">"} else {" "},
                id,squad["members"].as_array().map_or(0,Vec::len));
            text::draw_at(pixels, WIDTH, FIELD+14, 150+index*22,
                text::fit(&label,WIDTH-FIELD-24),1,
                if selected.contains(&id){TEXT}else{MUTED});
        }
        if let Some(squad)=first_selected(Some(state),selected) {
            let id=squad["owner"]["squad_id"].as_i64().unwrap_or(0);
            let shooting=squad["shooting"].as_u64().unwrap_or(0);
            text::draw_at(pixels,WIDTH,FIELD+14,417,
                &format!("Squad {id}  Shooting {shooting}"),1,TEXT);
            let enabled=|key:&str|if squad[key]==true {"On"} else {"Off"};
            let flags=format!("Chase {} Lock {} Speed {}",
                enabled("chasing"),enabled("locked_on"),enabled("match_speed"));
            text::draw_at(pixels,WIDTH,FIELD+14,435,
                text::fit(&flags,WIDTH-FIELD-24),1,MUTED);
        }
        let outcome=state["terminal_outcome"]["label"].as_str().map(str::to_owned)
            .or_else(||(!state["terminal_result"].is_null()).then(||
                format!("Winner side {}",state["terminal_result"])));
        if let Some(outcome)=outcome {
            text::draw_at(pixels,WIDTH,16,20,
                &format!("Result: {outcome}"),2,[255,210,129]);
        } else if let Some(objective)=state["campaign_objective"].as_object() {
            let remaining=objective.get("seconds_remaining").and_then(Value::as_f64);
            let elapsed=objective.get("elapsed_seconds").and_then(Value::as_f64);
            let duration=objective.get("duration_seconds").and_then(Value::as_f64);
            if let (Some(remaining),Some(elapsed),Some(duration))=(remaining,elapsed,duration) {
                if remaining.is_finite()&&elapsed.is_finite()&&
                    duration.is_finite()&&duration>0.0 {
                    let percent=(100.0*elapsed/duration).clamp(0.0,100.0);
                    let waves=objective.get("reinforcements_pending")
                        .and_then(Value::as_u64).unwrap_or(0);
                    let eta=objective.get("next_reinforcement_tick")
                        .and_then(Value::as_u64).map(|next|{
                            let tick=state["tick"].as_u64().unwrap_or(next);
                            let dt=state["fixed_step_seconds"].as_f64().unwrap_or(0.0);
                            ((next.saturating_sub(tick) as f64)*dt).max(0.0)
                        });
                    let status=if let Some(seconds)=eta.filter(|s|s.is_finite()) {
                        format!("Defend {:.0}s  {:.0}%  Waves {}  Next {:.0}s",
                            remaining,percent,waves,seconds)
                    }else {
                        format!("Defend {:.0}s  {:.0}%  Waves {}",
                            remaining,percent,waves)
                    };
                    text::draw_at(pixels,WIDTH,16,23,
                        text::fit(&status,FIELD-32),1,[255,211,130]);
                }
            }
        }
    } else {
        text::draw_at(pixels,WIDTH,30,30,"Waiting for authoritative battle state",2,TEXT);
    }
    if let Some(state)=state {draw_campaign_dialogue(pixels,state,dialogue_line);}
    if held_fire_active {
        text::draw_at(pixels,WIDTH,15,HEIGHT-44,
            "MANUAL FIRE - release J to stop",1,[255,221,151]);
    }
    let instructions=[
        "Left: select   Shift: add",
        "Drag: select multiple",
        "Right: move/attack/mine",
        "1-0 select Tab cycle A all",
        "F point fire J held fire",
        "G guard T attack H hold",
        "M mine Z cease fire",
        "C chase L lock V speed",
        "PgUp/Dn shooting (1-40)",
        "B beacon D detonate Q charge",
        "E heal W retreat P patrol",
        "R reset +/- zoom Arrows pan",
        "Esc cancel / disconnect",
    ];
    for (index,label) in instructions.iter().enumerate(){
        text::draw_at(pixels,WIDTH,FIELD+14,455+index*18,label,1,MUTED);
    }
    if let Some(mode)=mode {
        text::draw_at(pixels,WIDTH,15,HEIGHT-55,
            match mode {
                TargetMode::Fire=>"Click map to FIRE, Esc cancels",
                TargetMode::Attack=>"Click visible enemy squad to ATTACK",
                TargetMode::Guard=>"Click friendly squad to GUARD",
                TargetMode::Mine=>"Click mining asteroid to MINE",
                TargetMode::Heal=>"Click friendly Beehive to HEAL",
                TargetMode::Retreat=>"Click friendly WarpGate to RETREAT",
                TargetMode::Patrol=>"Click first / second PATROL corner",
            },1,[255,221,151]);
    }
    text::draw_at(pixels,WIDTH,10,HEIGHT-25,text::fit(notice,FIELD-20),1,TEXT);
    text::draw_at(pixels,WIDTH,FIELD+14,HEIGHT-24,
        &format!("RX {} KiB  updates {}",metrics.bytes_received/1024,metrics.snapshots),1,MUTED);
}

fn order_at(peer: &mut PeerClient, selected: &[i64], camera: NativeCamera,
    x: i32,y: i32, mode: Option<TargetMode>) -> Result<(),Box<dyn Error>> {
    if x<0||x>=FIELD as i32||y<0||y>=HEIGHT as i32{return Ok(())}
    let Some(state)=peer.state.as_ref() else{return Ok(())};
    let hit=hit(state,camera,x,y);
    let action=match (mode,hit) {
        (Some(TargetMode::Attack),Some(("ship",target))) |
        (None,Some(("ship",target))) if target["friendly"]==false => {
            let Some(squad)=squad_for_ship(state,target["id"].as_i64().unwrap_or(-1),
                target["side"].as_i64().unwrap_or(0)) else {
                return Err(invalid("enemy target has no visible squad").into())
            };
            ("support",json!({"action":{"Attack":{"target":squad["owner"]}}}))
        }
        (Some(TargetMode::Guard),Some(("ship",target))) |
        (None,Some(("ship",target))) if target["friendly"]==true => {
            let Some(squad)=squad_for_ship(state,target["id"].as_i64().unwrap_or(-1),
                target["side"].as_i64().unwrap_or(0)) else {
                return Err(invalid("friendly target has no visible squad").into())
            };
            ("support",json!({"action":{"Guard":{"target":squad["owner"]}}}))
        }
        (Some(TargetMode::Mine),Some(("obstacle",target))) |
        (None,Some(("obstacle",target))) if target["mining"]==true => {
            ("support",json!({"action":{"Mining":{"asteroid_id":target["id"]}}}))
        }
        (Some(TargetMode::Heal),Some(("ship",target))) if target["friendly"]==true => {
            ("support",json!({"action":{"Heal":{"beehive_ids":[target["id"]]}}}))
        }
        (Some(TargetMode::Retreat),Some(("ship",target))) if target["friendly"]==true => {
            ("support",json!({"action":{"FullRetreat":{"warp_gate_id":target["id"]}}}))
        }
        (Some(TargetMode::Attack|TargetMode::Guard|TargetMode::Mine|
              TargetMode::Heal|TargetMode::Retreat|TargetMode::Patrol),_) =>
            return Err(invalid("select a matching visible target").into()),
        (Some(TargetMode::Fire),_) => {
            let point=camera.screen_to_world(x as f32,y as f32,FIELD,HEIGHT);
            ("fire",json!({"target":{"x":point.x,"y":point.y}}))
        }
        (None,_) => {
            let point=camera.screen_to_world(x as f32,y as f32,FIELD,HEIGHT);
            ("move",json!({"destination":{"x":point.x,"y":point.y}}))
        }
    };
    peer.order(selected,action.0,action.1)
}

pub fn connect_remote(address: &str, token_file: &Path, expected_side: i32)
    -> Result<(), Box<dyn Error>> {
    if !(1..=2).contains(&expected_side) {
        return Err(invalid("remote player side must be 1 or 2").into());
    }
    let link=fs::symlink_metadata(token_file)?;
    if link.file_type().is_symlink()||!link.is_file()||link.len()>512 {
        return Err(invalid("remote token must be a private regular file").into());
    }
    #[cfg(unix)]
    {
        use std::os::unix::fs::PermissionsExt;
        if link.permissions().mode() & 0o077 != 0 {
            return Err(invalid("remote player token must have private file permissions").into());
        }
    }
    let secret=fs::read_to_string(token_file)?;
    let token=secret.trim();
    if !(24..=256).contains(&token.len()) ||token.chars().any(char::is_control) {
        return Err(invalid("invalid native multiplayer token").into());
    }
    let mut peer=PeerClient::connect(address,token,expected_side)?;
    let mut window=Window::new("Bees | Native Multiplayer",WIDTH,HEIGHT,
        WindowOptions::default())?;
    window.set_target_fps(30);
    let mut pixels=vec![0;WIDTH*HEIGHT];
    let mut camera:Option<NativeCamera>=None;
    let mut current_episode=(0_u64,0_u64);
    let mut selected:Vec<i64>=Vec::new();
    let mut mode:Option<TargetMode>=None;
    let mut patrol_start:Option<Vec2>=None;
    let mut last_left=false;
    let mut last_right=false;
    let mut drag_start:Option<(i32,i32)>=None;
    let mut last_middle:Option<(f32,f32)>=None;
    let mut dialogue_key:Option<(u64,u64,u64)>=None;
    let mut dialogue_line=0usize;
    let mut held_fire_active=false;
    let mut held_fire_target:Option<Vec2>=None;
    let started=Instant::now();

    while window.is_open() {
        if window.is_key_pressed(Key::Escape, KeyRepeat::No) {
            if mode.is_some() {
                mode=None;patrol_start=None;drag_start=None;
            } else {break;}
        }
        peer.poll()?;
        if peer.held_fire_rejected {
            held_fire_active = false;
            held_fire_target = None;
        }
        if !window.is_key_down(Key::J) {
            peer.held_fire_rejected = false;
        }
        if let Some(state)=peer.state.as_ref() {
            let cue=state["campaign_dialogue"]["sequence"].as_u64().map(|sequence|(
                state["world_id"].as_u64().unwrap_or(0),
                state["episode_revision"].as_u64().unwrap_or(0),sequence,
            ));
            if cue!=dialogue_key {dialogue_key=cue;dialogue_line=0;}
            let count=state["campaign_dialogue"]["line_count"]
                .as_u64().unwrap_or(0).min(100) as usize;
            if count>0 {
                if window.is_key_pressed(Key::Enter,KeyRepeat::No){
                    dialogue_line=(dialogue_line+1).min(count-1);
                }
                if window.is_key_pressed(Key::Backspace,KeyRepeat::No){
                    dialogue_line=dialogue_line.saturating_sub(1);
                }
            }
            let episode=(state["world_id"].as_u64().unwrap_or(0),
                         state["episode_revision"].as_u64().unwrap_or(0));
            if let Some(map)=bounds(state) {
                if camera.is_none()||episode !=current_episode {
                    camera=Some(NativeCamera::fit(map,FIELD,HEIGHT));
                    current_episode=episode;
                    selected.clear();
                    mode=None;
                    patrol_start=None;
                    // The server forgets this lease at the same authoritative
                    // episode boundary; the client never aims old ships.
                    held_fire_active=false;
                    held_fire_target=None;
                }
            }
            selected.retain(|id|state["squads"].as_array().into_iter().flatten()
                .any(|s|s["owned"]==true &&
                    s["owner"]["squad_id"].as_i64()==Some(*id)));
        }
        if let Some(mut cam)=camera {
            let keys=window.get_keys_pressed(KeyRepeat::No);
            for key in keys {
                let numbered=match key {
                    Key::Key1=>Some(1),Key::Key2=>Some(2),Key::Key3=>Some(3),
                    Key::Key4=>Some(4),Key::Key5=>Some(5),Key::Key6=>Some(6),
                    Key::Key7=>Some(7),Key::Key8=>Some(8),Key::Key9=>Some(9),
                    Key::Key0=>Some(0),_=>None,
                };
                if numbered.is_some()||key==Key::Tab {
                    if let Some(state)=peer.state.as_ref() {
                        let next=numbered.and_then(|number|numbered_squad(state,number))
                            .or_else(||(key==Key::Tab)
                                .then(||next_numbered_squad(state,&selected)).flatten());
                        if let Some(id)=next {
                            selected.clear();selected.push(id);
                            if let Some(point)=focus_squad(state,id){cam.center=point;}
                            mode=None;patrol_start=None;
                            peer.notice=format!("Selected source squad {id}");
                        } else {
                            peer.notice="No surviving authored numbered squad for that key".into();
                        }
                    }
                    continue;
                }
                let send=match key {
                    Key::A => {
                        if let Some(state)=peer.state.as_ref() {
                            selected=state["squads"].as_array().into_iter().flatten()
                                .filter(|s|s["owned"]==true)
                                .filter_map(|s|s["owner"]["squad_id"].as_i64()).collect();
                        }
                        None
                    }
                    Key::F=>{mode=Some(TargetMode::Fire);patrol_start=None;None}
                    Key::T=>{mode=Some(TargetMode::Attack);patrol_start=None;None}
                    Key::G=>{mode=Some(TargetMode::Guard);patrol_start=None;None}
                    Key::M=>{mode=Some(TargetMode::Mine);patrol_start=None;None}
                    Key::E=>{mode=Some(TargetMode::Heal);patrol_start=None;None}
                    Key::W=>{mode=Some(TargetMode::Retreat);patrol_start=None;None}
                    Key::P=>{mode=Some(TargetMode::Patrol);patrol_start=None;None}
                    Key::R=>{
                        if let Some(map)=peer.state.as_ref().and_then(bounds) {
                            cam=NativeCamera::fit(map,FIELD,HEIGHT);
                        }
                        None
                    }
                    Key::H=>Some(("support",json!({"action":"Hold"}))),
                    Key::Z=>Some(("cease_fire",json!({
                        "cease_fire":!first_selected(peer.state.as_ref(),&selected)
                            .and_then(|s|s["cease_fire"].as_bool()).unwrap_or(false)
                    }))),
                    Key::C=>Some(("chase",json!({
                        "chasing":!first_selected(peer.state.as_ref(),&selected)
                            .and_then(|s|s["chasing"].as_bool()).unwrap_or(false)
                    }))),
                    Key::L=>Some(("lock_on",json!({
                        "locked":!first_selected(peer.state.as_ref(),&selected)
                            .and_then(|s|s["locked_on"].as_bool()).unwrap_or(false)
                    }))),
                    Key::V=>Some(("match_speed",json!({
                        "match_speed":!first_selected(peer.state.as_ref(),&selected)
                            .and_then(|s|s["match_speed"].as_bool()).unwrap_or(false)
                    }))),
                    Key::B=>Some(("special",json!({"kind":"DropBeacon"}))),
                    Key::D=>Some(("special",json!({"kind":"Detonate"}))),
                    Key::Q=>Some(("special",json!({"kind":"Charge"}))),
                    Key::PageUp|Key::PageDown=>{
                        let previous=first_selected(peer.state.as_ref(),&selected)
                            .and_then(|s|s["shooting"].as_u64()).unwrap_or(1).clamp(1,40);
                        let next=if key==Key::PageUp{
                            (previous+1).min(40)
                        }else{previous.saturating_sub(1).max(1)};
                        Some(("shooting",json!({"strategy_id":next})))
                    }
                    _=>None,
                };
                if let Some((name,fields))=send {
                    if let Err(error)=peer.order(&selected,name,fields) {
                        peer.notice=error.to_string();
                    }
                }
            }
            let pan=24.0/cam.pixels_per_unit;
            if window.is_key_down(Key::Up){cam.center.y+=pan;}
            if window.is_key_down(Key::Down){cam.center.y-=pan;}
            if window.is_key_down(Key::Left){cam.center.x-=pan;}
            if window.is_key_down(Key::Right){cam.center.x+=pan;}
            if let Some((_,wheel))=window.get_scroll_wheel(){
                if wheel!=0.0 {cam.zoom_at((1.15_f32).powf(wheel),FIELD as f32/2.0,
                    HEIGHT as f32/2.0,FIELD,HEIGHT,None);}
            }
            if window.is_key_down(Key::Equal){cam.zoom_at(1.035,FIELD as f32/2.0,
                HEIGHT as f32/2.0,FIELD,HEIGHT,None);}
            if window.is_key_down(Key::Minus){cam.zoom_at(1.0/1.035,
                FIELD as f32/2.0,HEIGHT as f32/2.0,FIELD,HEIGHT,None);}
            if let Some(map)=peer.state.as_ref().and_then(bounds) {
                cam.constrain_to_map(map,FIELD,HEIGHT);
            }
            let mouse=window.get_mouse_pos(MouseMode::Discard);
            let within=mouse.and_then(|(x,y)| {
                (x>=0.0 && x<FIELD as f32 && y>=0.0 && y<HEIGHT as f32)
                    .then_some((x as i32,y as i32))
            });
            let holding=window.is_active() && window.is_key_down(Key::J)
                && !peer.held_fire_rejected && mode.is_none() && within.is_some() &&
                (held_fire_active || !selected.is_empty());
            match (held_fire_active,holding,within) {
                (false,true,Some((x,y))) => {
                    let point=cam.screen_to_world(x as f32,y as f32,FIELD,HEIGHT);
                    match peer.held_fire(&selected,"Begin",Some(point)) {
                        Ok(()) => {
                            held_fire_active=true;held_fire_target=Some(point);
                        },
                        Err(error) => peer.notice=error.to_string(),
                    }
                },
                (true,true,Some((x,y))) => {
                    let point=cam.screen_to_world(x as f32,y as f32,FIELD,HEIGHT);
                    // Camera movement also changes the world aim when the
                    // mouse has not moved. Skip truly unchanged targets.
                    if !held_fire_target.is_some_and(|prior| prior.unity_equal(point)) {
                        match peer.held_fire(&selected,"Aim",Some(point)) {
                            Ok(()) => held_fire_target=Some(point),
                            Err(error) => peer.notice=error.to_string(),
                        }
                    }
                },
                (true,false,_) => {
                    if let Err(error)=peer.held_fire(&selected,"End",None) {
                        // If a bounded outgoing queue cannot transmit End,
                        // close the socket to trigger the host's authoritative
                        // disconnect release. Never leave a stale fire lock.
                        let _=peer.socket.shutdown(std::net::Shutdown::Both);
                        peer.connected=false;
                        peer.notice=error.to_string();
                    }
                    held_fire_active=false;held_fire_target=None;
                },
                _ => {},
            }
            let left=window.get_mouse_down(MouseButton::Left);
            let right=window.get_mouse_down(MouseButton::Right);
            let middle=window.get_mouse_down(MouseButton::Middle);
            if middle {
                if let (Some((x,y)),Some((old_x,old_y)))=(mouse,last_middle) {
                    cam.center.x-=(x-old_x)/cam.pixels_per_unit;
                    cam.center.y+=(y-old_y)/cam.pixels_per_unit;
                }
                last_middle=mouse;
            }else{last_middle=None;}
            if let Some((x,y))=mouse {
                let (x,y)=(x as i32,y as i32);
                if left&&!last_left&&x>=0&&x<FIELD as i32 {
                    drag_start=Some((x,y));
                }
                if !left&&last_left {
                    if mode.is_some() {
                        drag_start=None;
                        if x>=0 && x<FIELD as i32 && y>=0 && y<HEIGHT as i32 {
                            if mode == Some(TargetMode::Patrol) {
                                let point=cam.screen_to_world(x as f32,y as f32,FIELD,HEIGHT);
                                if let Some(first)=patrol_start {
                                    let fields=json!({"action":{"Patrol":{
                                        "top_left":{"x":first.x.min(point.x),
                                                    "y":first.y.max(point.y)},
                                        "bottom_right":{"x":first.x.max(point.x),
                                                        "y":first.y.min(point.y)}
                                    }}});
                                    if let Err(error)=peer.order(&selected,"support",fields) {
                                        peer.notice=error.to_string();
                                    } else {
                                        mode=None;
                                        patrol_start=None;
                                    }
                                } else {
                                    patrol_start=Some(point);
                                    peer.notice="Choose the second patrol corner".into();
                                }
                            } else if let Err(error)=order_at(&mut peer,&selected,cam,x,y,mode) {
                                peer.notice=error.to_string();
                            } else {mode=None;}
                        }
                    } else if let Some((sx,sy))=drag_start.take() {
                        if let Some(state)=peer.state.as_ref() {
                            let additive=window.is_key_down(Key::LeftShift)||
                                window.is_key_down(Key::RightShift);
                            let mut additions=Vec::new();
                            if (x-sx).abs()+(y-sy).abs()>8 {
                                for ship in state["ships"].as_array().into_iter().flatten(){
                                    if ship["owned"]!=true {continue;}
                                    let Some(p)=position(ship) else {continue;};
                                    let (px,py)=screen(cam,p);
                                    if px>=sx.min(x)&&px<=sx.max(x)&&
                                        py>=sy.min(y)&&py<=sy.max(y) {
                                        if let Some(squad)=squad_for_ship(state,
                                            ship["id"].as_i64().unwrap_or(-1),
                                            ship["side"].as_i64().unwrap_or(0)){
                                            if let Some(id)=squad["owner"]["squad_id"].as_i64(){
                                                if !additions.contains(&id){additions.push(id);}
                                            }
                                        }
                                    }
                                }
                            } else if let Some(("ship",ship))=hit(state,cam,x,y) {
                                if ship["owned"]==true {
                                    if let Some(squad)=squad_for_ship(state,
                                        ship["id"].as_i64().unwrap_or(-1),
                                        ship["side"].as_i64().unwrap_or(0)) {
                                        if let Some(id)=squad["owner"]["squad_id"].as_i64() {
                                            additions.push(id);
                                        }
                                    }
                                }
                            }
                            if !additive {selected.clear();}
                            for id in additions {
                                if !selected.contains(&id){selected.push(id);}
                            }
                        }
                    }
                }
                if right&&!last_right {
                    if let Err(error)=order_at(&mut peer,&selected,cam,x,y,None) {
                        peer.notice=error.to_string();
                    }
                }
            }
            last_left=left;last_right=right;
            camera=Some(cam);
        }
        draw(&mut pixels,peer.state.as_ref(),camera,&selected,&peer.notice,
            expected_side,mode,patrol_start,&peer.metrics,dialogue_line,
            held_fire_active);
        window.update_with_buffer(&pixels,WIDTH,HEIGHT)?;
    }
    println!("{}",json!({
        "kind":"native_multiplayer_client_final",
        "side":expected_side,
        "session_wall_seconds":started.elapsed().as_secs_f64(),
        "bytes_received":peer.metrics.bytes_received,
        "snapshots_rendered":peer.metrics.snapshots,
        "orders_sent":peer.metrics.orders_sent,
        "order_acknowledgments":peer.metrics.acknowledgments,
        "connection_closed":!peer.connected,
    }));
    Ok(())
}
