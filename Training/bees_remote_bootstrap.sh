#!/usr/bin/env bash
set -euo pipefail

DEFAULT_INSTALL_ROOT="__BEES_LINUX_INSTALL_ROOT__"
DEFAULT_TORCH_DEVICE="__BEES_TORCH_DEVICE__"
TAILNET_LEARNER="__BEES_TAILNET_LEARNER__"
TAILNET_BOOTSTRAP_PORT="__BEES_TAILNET_BOOTSTRAP_PORT__"
CONTROL_PORT="__BEES_CONTROL_PORT__"
BROKER_PORT="__BEES_BROKER_PORT__"
GAMEPLAY_PORT="__BEES_GAMEPLAY_PORT__"
BUNDLED_TAILNET_BRIDGE="__BEES_TAILNET_BRIDGE_FILE__"
TAILNET_BRIDGE_SHA256="__BEES_TAILNET_BRIDGE_SHA256__"
BOOTSTRAP_TOKEN="__BEES_BOOTSTRAP_TOKEN__"

COMMAND="start"
ENVS=""
INSTALL_ROOT="$DEFAULT_INSTALL_ROOT"
TORCH_DEVICE="$DEFAULT_TORCH_DEVICE"
NO_AUTOSTART=0

usage() {
    cat <<'EOF'
Usage: bees-remote-worker.sh [start|stop] [options]

Commands:
  start                  Start the worker in the background (default).
  stop                   Gracefully stop the background worker.

Options:
  --envs N               Pin a fixed Unity environment count (1-64); omission auto-tunes.
  --install-root PATH    Local Linux worker installation directory.
  --torch-device DEVICE  Local inference device, normally cpu or cuda.
  --no-autostart          Do not register this worker to restart after reboot/login.
  -h, --help             Show this help.
EOF
}

if [[ $# -gt 0 && ( "$1" == "start" || "$1" == "stop" ) ]]; then
    COMMAND="$1"
    shift
fi

while [[ $# -gt 0 ]]; do
    case "$1" in
        --envs) ENVS="$2"; shift 2 ;;
        --install-root) INSTALL_ROOT="$2"; shift 2 ;;
        --torch-device) TORCH_DEVICE="$2"; shift 2 ;;
        --no-autostart) NO_AUTOSTART=1; shift ;;
        -h|--help) usage; exit 0 ;;
        *) echo "error: unknown argument: $1" >&2; usage >&2; exit 2 ;;
    esac
done

for value in "$INSTALL_ROOT" "$TORCH_DEVICE" "$TAILNET_LEARNER" "$TAILNET_BOOTSTRAP_PORT" "$CONTROL_PORT" "$BROKER_PORT" "$GAMEPLAY_PORT" "$BUNDLED_TAILNET_BRIDGE" "$TAILNET_BRIDGE_SHA256" "$BOOTSTRAP_TOKEN"; do
    if [[ "$value" == __BEES_* ]]; then
        echo "error: launcher is not configured. Copy the generated .sh file from B:\\Bees\\Remote after running bees.ps1 start." >&2
        exit 2
    fi
done

is_uint() { [[ "$1" =~ ^[0-9]+$ ]]; }
if [[ -n "$ENVS" ]] && { ! is_uint "$ENVS" || (( ENVS < 1 || ENVS > 64 )); }; then
    echo "error: --envs must be in 1-64 when specified" >&2
    exit 2
fi
for port in "$TAILNET_BOOTSTRAP_PORT" "$CONTROL_PORT" "$BROKER_PORT" "$GAMEPLAY_PORT"; do
    if ! is_uint "$port" || (( port < 1 || port > 65535 )); then
        echo "error: configured Bees ports must be in 1-65535" >&2
        exit 2
    fi
done

if [[ "$INSTALL_ROOT" == "~" ]]; then
    INSTALL_ROOT="$HOME"
elif [[ "$INSTALL_ROOT" == "~/"* ]]; then
    INSTALL_ROOT="$HOME/${INSTALL_ROOT#~/}"
elif [[ "$INSTALL_ROOT" != /* ]]; then
    INSTALL_ROOT="$HOME/$INSTALL_ROOT"
fi

have() { command -v "$1" >/dev/null 2>&1; }

AUTOSTART_UNIT="$HOME/.config/systemd/user/bees-training-worker.service"
AUTOSTART_MARKER="$INSTALL_ROOT/remote-autostart.enabled"
AUTOSTART_MONITOR="$INSTALL_ROOT/Launcher/bees-remote-monitor.sh"
AUTOSTART_MONITOR_LOG="$INSTALL_ROOT/Logs/remote-monitor.log"
AUTOSTART_CHILD="${BEES_AUTOSTART_CHILD:-0}"

systemd_quote() {
    local value="$1"
    value="${value//\\/\\\\}"
    value="${value//\"/\\\"}"
    printf '"%s"' "$value"
}

remove_remote_autostart() {
    rm -f "$AUTOSTART_MARKER"
    if have systemctl; then
        systemctl --user disable bees-training-worker.service >/dev/null 2>&1 || true
        systemctl --user stop bees-training-worker.service >/dev/null 2>&1 || true
    fi
    rm -f "$AUTOSTART_UNIT"
    if have systemctl; then
        systemctl --user daemon-reload >/dev/null 2>&1 || true
    fi
}

install_remote_autostart() {
    if [[ "$AUTOSTART_CHILD" == "1" ]]; then
        return
    fi
    if (( NO_AUTOSTART )); then
        remove_remote_autostart
        return
    fi
    local launcher="${BEES_REMOTE_LAUNCHER_PATH:-}"
    if [[ -z "$launcher" || ! -f "$launcher" ]]; then
        echo "warning: copied launcher path is unavailable; reboot autostart could not be registered." >&2
        return
    fi
    if ! have systemctl; then
        echo "warning: systemd user services are unavailable; reboot autostart could not be registered." >&2
        return
    fi
    mkdir -p "$(dirname "$AUTOSTART_UNIT")" "$(dirname "$AUTOSTART_MONITOR")" "$(dirname "$AUTOSTART_MONITOR_LOG")"
    printf 'enabled\n' > "$AUTOSTART_MARKER"

    local q_marker q_launcher q_install q_torch q_log
    printf -v q_marker '%q' "$AUTOSTART_MARKER"
    printf -v q_launcher '%q' "$launcher"
    printf -v q_install '%q' "$INSTALL_ROOT"
    printf -v q_torch '%q' "$TORCH_DEVICE"
    printf -v q_log '%q' "$AUTOSTART_MONITOR_LOG"
    cat > "$AUTOSTART_MONITOR" <<EOF
#!/usr/bin/env bash
set -u
MARKER=$q_marker
LAUNCHER=$q_launcher
INSTALL_ROOT=$q_install
TORCH_DEVICE=$q_torch
MONITOR_LOG=$q_log
CONTROL_PORT=$CONTROL_PORT
ENVS=$ENVS

supervisor_alive() {
    local pid_file="\$INSTALL_ROOT/remote-worker.pid"
    local pid="" part="" command_line=""
    [[ -f "\$pid_file" ]] || return 1
    IFS= read -r pid < "\$pid_file" || true
    [[ "\$pid" =~ ^[0-9]+$ ]] || return 1
    kill -0 "\$pid" 2>/dev/null || return 1
    [[ -r "/proc/\$pid/cmdline" ]] || return 1
    while IFS= read -r -d '' part; do
        command_line+="\$part "
    done < "/proc/\$pid/cmdline"
    [[ "\$command_line" == *"bees_managed_remote_worker.py"* && "\$command_line" == *"\$INSTALL_ROOT"* ]]
}

control_probe_once() {
    local python="\$INSTALL_ROOT/.venv/bin/python"
    local token_file="\$INSTALL_ROOT/Secrets/training-worker.token"
    [[ -x "\$python" && -s "\$token_file" ]] || return 1
    "\$python" - "\$CONTROL_PORT" "\$token_file" "\$INSTALL_ROOT" <<'PY'
import json
import socket
import sys
import urllib.request
from pathlib import Path

port, token_file, install_root = sys.argv[1], sys.argv[2], sys.argv[3]
try:
    token = Path(token_file).read_text(encoding="utf-8").strip()
    actor_key = (Path(install_root) / "actor-key.txt").read_text(
        encoding="ascii"
    ).strip().lower()
    if len(actor_key) != 32 or any(ch not in "0123456789abcdef" for ch in actor_key):
        raise ValueError("invalid actor key")
    trainer_id = f"remote-{socket.gethostname().lower()}-{actor_key[:8]}"
    request = urllib.request.Request(
        f"http://127.0.0.1:{port}/v1/status",
        headers={"Authorization": "Bearer " + token},
    )
    with urllib.request.urlopen(request, timeout=3.0) as response:
        status = json.load(response)
    record = next(
        (item for item in status.get("trainers", ())
         if str(item.get("trainer_id", "")) == trainer_id),
        None,
    )
    if not isinstance(record, dict) or bool(record.get("stale", False)):
        raise SystemExit(1)
    desired = status.get("desired") if isinstance(status.get("desired"), dict) else {}
    stalled = (
        bool(desired.get("training_enabled", False))
        and desired.get("pending_release") is None
        and str(record.get("process_state", "")) == "stopped"
        and str(record.get("last_error", "")).startswith("ControlUnavailable:")
    )
    raise SystemExit(1 if stalled else 0)
except Exception:
    raise SystemExit(1)
PY
}

control_healthy() {
    local attempt=0
    for attempt in 1 2 3; do
        control_probe_once && return 0
        (( attempt < 3 )) && sleep 0.5
    done
    return 1
}

launch_worker() {
    if [[ -n "\$ENVS" ]]; then
        BEES_AUTOSTART_CHILD=1 bash "\$LAUNCHER" start --install-root "\$INSTALL_ROOT" --torch-device "\$TORCH_DEVICE" --envs "\$ENVS" >>"\$MONITOR_LOG" 2>&1 || true
    else
        BEES_AUTOSTART_CHILD=1 bash "\$LAUNCHER" start --install-root "\$INSTALL_ROOT" --torch-device "\$TORCH_DEVICE" >>"\$MONITOR_LOG" 2>&1 || true
    fi
}

CONTROL_FAILURES=0
while [[ -f "\$MARKER" ]]; do
    if ! supervisor_alive; then
        CONTROL_FAILURES=0
        launch_worker
    elif control_healthy; then
        CONTROL_FAILURES=0
    else
        CONTROL_FAILURES=\$((CONTROL_FAILURES + 1))
        if (( CONTROL_FAILURES >= 3 )); then
            printf '[Bees remote] watchdog observed repeated authenticated control failures; invoking launcher repair.\n' >>"\$MONITOR_LOG"
            launch_worker
            CONTROL_FAILURES=0
        fi
    fi
    sleep 10
done
EOF
    chmod 700 "$AUTOSTART_MONITOR"

    cat > "$AUTOSTART_UNIT" <<EOF
[Unit]
Description=Bees remote training worker watchdog
After=network-online.target
Wants=network-online.target

[Service]
Type=simple
ExecStart=/bin/bash $(systemd_quote "$AUTOSTART_MONITOR")
Restart=always
RestartSec=5

[Install]
WantedBy=default.target
EOF
    systemctl --user daemon-reload >/dev/null 2>&1 || {
        echo "warning: systemd user manager is unavailable; reboot autostart could not be enabled." >&2
        return
    }
    systemctl --user enable bees-training-worker.service >/dev/null 2>&1 || {
        echo "warning: could not enable Bees user autostart service." >&2
        return
    }
    systemctl --user restart bees-training-worker.service >/dev/null 2>&1 || {
        echo "warning: Bees user watchdog could not be started immediately." >&2
    }

    if have loginctl; then
        local linger=""
        linger="$(loginctl show-user "$(id -un)" -p Linger --value 2>/dev/null || true)"
        if [[ "$linger" != "yes" ]]; then
            if [[ "$(id -u)" -eq 0 ]]; then
                loginctl enable-linger "$(id -un)" >/dev/null 2>&1 || true
            elif have sudo && sudo -n true >/dev/null 2>&1; then
                sudo -n loginctl enable-linger "$(id -un)" >/dev/null 2>&1 || true
            fi
            linger="$(loginctl show-user "$(id -un)" -p Linger --value 2>/dev/null || true)"
            if [[ "$linger" != "yes" ]]; then
                echo "warning: Bees autostart is enabled for login, but unattended boot requires: sudo loginctl enable-linger $(id -un)" >&2
            fi
        fi
    fi
}

mkdir -p "$INSTALL_ROOT"

# Manual starts and the user-systemd watchdog can invoke this launcher concurrently. Serialize the
# entire bootstrap/repair transaction so runtime extraction, token replacement, Tailnet state,
# Python environment mutation, and supervisor stop/start cannot overlap.
BOOTSTRAP_LOCK_FILE="$INSTALL_ROOT/remote-bootstrap.lock"
BOOTSTRAP_LOCK_LINK="$INSTALL_ROOT/remote-bootstrap.lock.owner"
BOOTSTRAP_LOCK_METHOD=""
BOOTSTRAP_LOCK_FD=""

acquire_bootstrap_lock() {
    local deadline=$((SECONDS + 60))
    if have flock; then
        exec {BOOTSTRAP_LOCK_FD}>"$BOOTSTRAP_LOCK_FILE"
        if ! flock -w 60 "$BOOTSTRAP_LOCK_FD"; then
            echo "error: another Bees remote bootstrap/repair is still active after 60 seconds." >&2
            exit 1
        fi
        BOOTSTRAP_LOCK_METHOD="flock"
        return
    fi

    # Portable fallback for minimal systems without util-linux flock. The symlink target publishes
    # the owner PID atomically, so there is no mkdir/write gap that can leave an ownerless lock.
    while ! ln -s "$$" "$BOOTSTRAP_LOCK_LINK" 2>/dev/null; do
        local owner=""
        owner="$(readlink "$BOOTSTRAP_LOCK_LINK" 2>/dev/null || true)"
        if [[ "$owner" =~ ^[0-9]+$ ]] && ! kill -0 "$owner" 2>/dev/null; then
            rm -f "$BOOTSTRAP_LOCK_LINK"
            continue
        fi
        if (( SECONDS >= deadline )); then
            echo "error: another Bees remote bootstrap/repair is still active after 60 seconds." >&2
            exit 1
        fi
        sleep 0.25
    done
    BOOTSTRAP_LOCK_METHOD="symlink"
}

release_bootstrap_lock() {
    if [[ "$BOOTSTRAP_LOCK_METHOD" == "flock" && -n "$BOOTSTRAP_LOCK_FD" ]]; then
        flock -u "$BOOTSTRAP_LOCK_FD" >/dev/null 2>&1 || true
        exec {BOOTSTRAP_LOCK_FD}>&-
    elif [[ "$BOOTSTRAP_LOCK_METHOD" == "symlink" ]]; then
        local owner=""
        owner="$(readlink "$BOOTSTRAP_LOCK_LINK" 2>/dev/null || true)"
        if [[ "$owner" == "$$" ]]; then
            rm -f "$BOOTSTRAP_LOCK_LINK"
        fi
    fi
}

acquire_bootstrap_lock
trap release_bootstrap_lock EXIT

SUPERVISOR_PID_FILE="$INSTALL_ROOT/remote-worker.pid"
SHUTDOWN_REQUEST_FILE="$INSTALL_ROOT/remote-worker.stop"
LOGS_ROOT="$INSTALL_ROOT/Logs"
SUPERVISOR_LOG="$LOGS_ROOT/remote-supervisor.log"

recorded_pid() {
    if [[ ! -f "$SUPERVISOR_PID_FILE" ]]; then
        return 1
    fi
    local value=""
    IFS= read -r value < "$SUPERVISOR_PID_FILE" || true
    if [[ ! "$value" =~ ^[0-9]+$ ]] || (( value <= 0 )); then
        return 1
    fi
    printf '%s' "$value"
}

pid_is_supervisor() {
    local pid="$1"
    local part=""
    local command_line=""
    if ! kill -0 "$pid" 2>/dev/null || [[ ! -r "/proc/$pid/cmdline" ]]; then
        return 1
    fi
    while IFS= read -r -d '' part; do
        command_line+="$part "
    done < "/proc/$pid/cmdline"
    [[ "$command_line" == *"bees_managed_remote_worker.py"* && "$command_line" == *"$INSTALL_ROOT"* ]]
}

supervisor_control_probe_once() {
    local python="$INSTALL_ROOT/.venv/bin/python"
    local token_file="$INSTALL_ROOT/Secrets/training-worker.token"
    [[ -x "$python" && -s "$token_file" ]] || return 1
    "$python" - "$CONTROL_PORT" "$token_file" "$INSTALL_ROOT" <<'PY'
import json
import socket
import sys
import urllib.request
from pathlib import Path

port, token_file, install_root = sys.argv[1], sys.argv[2], sys.argv[3]
try:
    token = Path(token_file).read_text(encoding="utf-8").strip()
    actor_key = (Path(install_root) / "actor-key.txt").read_text(
        encoding="ascii"
    ).strip().lower()
    if len(actor_key) != 32 or any(ch not in "0123456789abcdef" for ch in actor_key):
        raise ValueError("invalid actor key")
    trainer_id = f"remote-{socket.gethostname().lower()}-{actor_key[:8]}"
    request = urllib.request.Request(
        f"http://127.0.0.1:{port}/v1/status",
        headers={"Authorization": "Bearer " + token},
    )
    with urllib.request.urlopen(request, timeout=3.0) as response:
        status = json.load(response)
    record = next(
        (item for item in status.get("trainers", ())
         if str(item.get("trainer_id", "")) == trainer_id),
        None,
    )
    if not isinstance(record, dict) or bool(record.get("stale", False)):
        raise SystemExit(1)
    desired = status.get("desired") if isinstance(status.get("desired"), dict) else {}
    stalled = (
        bool(desired.get("training_enabled", False))
        and desired.get("pending_release") is None
        and str(record.get("process_state", "")) == "stopped"
        and str(record.get("last_error", "")).startswith("ControlUnavailable:")
    )
    raise SystemExit(1 if stalled else 0)
except Exception:
    raise SystemExit(1)
PY
}

supervisor_control_healthy() {
    local attempt=0
    for attempt in 1 2 3; do
        supervisor_control_probe_once && return 0
        (( attempt < 3 )) && sleep 0.5
    done
    return 1
}

restart_unhealthy_supervisor() {
    local pid="$1"
    if [[ "$AUTOSTART_CHILD" != "1" ]] && have systemctl; then
        # Prevent the user-systemd watchdog from racing this deliberate repair.
        systemctl --user stop bees-training-worker.service >/dev/null 2>&1 || true
    fi

    echo "warning: [Bees remote] supervisor PID $pid is alive but authenticated learner control is not healthy; recycling it before bootstrap." >&2
    printf 'stop' > "$SHUTDOWN_REQUEST_FILE"
    local attempts=0
    while kill -0 "$pid" 2>/dev/null && (( attempts < 80 )); do
        sleep 0.25
        attempts=$((attempts + 1))
    done
    if ! kill -0 "$pid" 2>/dev/null; then
        rm -f "$SUPERVISOR_PID_FILE" "$SHUTDOWN_REQUEST_FILE"
        echo "[Bees remote] unhealthy supervisor stopped cleanly; continuing bootstrap."
        return 0
    fi

    echo "warning: [Bees remote] unhealthy supervisor did not stop cleanly within 20 seconds; forcing the stale remote supervisor to terminate." >&2
    kill -KILL "$pid" 2>/dev/null || true
    attempts=0
    while kill -0 "$pid" 2>/dev/null && (( attempts < 40 )); do
        sleep 0.25
        attempts=$((attempts + 1))
    done
    if kill -0 "$pid" 2>/dev/null; then
        echo "error: could not terminate unhealthy remote supervisor PID $pid." >&2
        return 1
    fi
    rm -f "$SUPERVISOR_PID_FILE" "$SHUTDOWN_REQUEST_FILE"
}
if [[ "$COMMAND" == "stop" ]]; then
    remove_remote_autostart
    PID="$(recorded_pid || true)"
    if [[ -z "$PID" ]] || ! pid_is_supervisor "$PID"; then
        rm -f "$SUPERVISOR_PID_FILE" "$SHUTDOWN_REQUEST_FILE"
        echo "[Bees remote] worker is not running."
        exit 0
    fi

    printf 'stop' > "$SHUTDOWN_REQUEST_FILE"
    echo "[Bees remote] stop requested for worker PID $PID; waiting for managed cleanup..."
    ATTEMPTS=0
    while kill -0 "$PID" 2>/dev/null && (( ATTEMPTS < 180 )); do
        sleep 0.25
        ATTEMPTS=$((ATTEMPTS + 1))
    done
    if kill -0 "$PID" 2>/dev/null; then
        echo "error: remote worker PID $PID did not stop within 45 seconds; it was not force-killed." >&2
        exit 1
    fi
    rm -f "$SUPERVISOR_PID_FILE" "$SHUTDOWN_REQUEST_FILE"
    echo "[Bees remote] worker stopped."
    exit 0
fi

PID="$(recorded_pid || true)"
if [[ -n "$PID" ]] && pid_is_supervisor "$PID"; then
    if supervisor_control_healthy; then
        install_remote_autostart
        echo "[Bees remote] worker is already running and authenticated learner control is healthy (PID $PID)."
        echo "[Bees remote] use 'bash bees-remote-worker.sh stop' to stop it."
        exit 0
    fi
    restart_unhealthy_supervisor "$PID"
fi
rm -f "$SUPERVISOR_PID_FILE" "$SHUTDOWN_REQUEST_FILE"

sudo_cmd() {
    if [[ "$(id -u)" -eq 0 ]]; then
        "$@"
    elif have sudo; then
        sudo "$@"
    else
        echo "error: root privileges are required to install a missing base utility, but sudo is unavailable." >&2
        return 1
    fi
}

install_system_tools() {
    if have apt-get; then
        sudo_cmd apt-get update
        sudo_cmd apt-get install -y curl ca-certificates coreutils
    elif have dnf; then
        sudo_cmd dnf install -y curl ca-certificates coreutils
    elif have yum; then
        sudo_cmd yum install -y curl ca-certificates coreutils
    elif have zypper; then
        sudo_cmd zypper --non-interactive install curl ca-certificates coreutils
    elif have pacman; then
        sudo_cmd pacman -Sy --noconfirm curl ca-certificates coreutils
    else
        echo "error: required base utilities are missing and no supported package manager was found." >&2
        return 1
    fi
}

if ! have base64 || ! have nohup || { ! have sha256sum && ! have shasum; } || { ! have curl && ! have wget; }; then
    echo "[Bees remote] installing missing base system prerequisites..."
    install_system_tools
fi
if ! have base64 || ! have nohup || { ! have sha256sum && ! have shasum; }; then
    echo "error: base64, nohup, and SHA-256 utilities are required." >&2
    exit 2
fi

echo "[Bees remote] Stage 1/5: preparing local worker files..."
RUNTIME_ROOT="$INSTALL_ROOT/Runtime"
SECRETS_ROOT="$INSTALL_ROOT/Secrets"
DOWNLOADS_ROOT="$INSTALL_ROOT/Downloads"
VENV_ROOT="$INSTALL_ROOT/.venv"
TAILNET_ROOT="$INSTALL_ROOT/Tailnet"
TAILNET_STATE="$TAILNET_ROOT/State"
mkdir -p "$RUNTIME_ROOT" "$SECRETS_ROOT" "$DOWNLOADS_ROOT" "$TAILNET_ROOT" "$TAILNET_STATE" "$LOGS_ROOT"

hash_file() {
    if have sha256sum; then
        sha256sum "$1" | awk '{print $1}'
    else
        shasum -a 256 "$1" | awk '{print $1}'
    fi
}

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" >/dev/null 2>&1 && pwd)"
BUNDLED_BRIDGE_PATH="$SCRIPT_DIR/$BUNDLED_TAILNET_BRIDGE"
if [[ ! -f "$BUNDLED_BRIDGE_PATH" ]]; then
    echo "error: bundled tailnet runtime is missing: $BUNDLED_BRIDGE_PATH" >&2
    echo "Copy the generated Linux remote bundle files together." >&2
    exit 2
fi
if [[ "$(hash_file "$BUNDLED_BRIDGE_PATH")" != "$TAILNET_BRIDGE_SHA256" ]]; then
    echo "error: bundled tailnet runtime failed SHA-256 verification." >&2
    exit 2
fi

TAILNET_BRIDGE="$TAILNET_ROOT/bees-tailnet-bridge"
WRITE_BRIDGE=1
if [[ -f "$TAILNET_BRIDGE" ]] && [[ "$(hash_file "$TAILNET_BRIDGE")" == "$TAILNET_BRIDGE_SHA256" ]]; then
    WRITE_BRIDGE=0
fi
if (( WRITE_BRIDGE )); then
    echo "[Bees remote] installing bundled private-network runtime..."
    cp "$BUNDLED_BRIDGE_PATH" "$TAILNET_BRIDGE"
    chmod 700 "$TAILNET_BRIDGE"
fi
if [[ "$(hash_file "$TAILNET_BRIDGE")" != "$TAILNET_BRIDGE_SHA256" ]]; then
    echo "error: installed tailnet runtime failed SHA-256 verification." >&2
    exit 2
fi

BOOTSTRAP_TOKEN_FILE="$TAILNET_ROOT/bootstrap.token"
printf '%s' "$BOOTSTRAP_TOKEN" > "$BOOTSTRAP_TOKEN_FILE"
chmod 600 "$BOOTSTRAP_TOKEN_FILE"

HOST_PART="$(hostname 2>/dev/null || printf 'linux')"
HOST_PART="${HOST_PART//[^A-Za-z0-9-]/-}"
HOST_PART="${HOST_PART,,}"
WORKER_HOSTNAME="bees-worker-$HOST_PART"

echo "[Bees remote] Stage 2/5: checking private-network identity..."
echo "[Bees remote] on first use, open the Tailscale login URL printed below; no VPN installation is required."
"$TAILNET_BRIDGE" auth --state "$TAILNET_STATE" --hostname "$WORKER_HOSTNAME"

RUNTIME_ZIP="$DOWNLOADS_ROOT/bees-remote-runtime.zip"
WORKER_TOKEN="$SECRETS_ROOT/training-worker.token"
WAN_TOKEN="$SECRETS_ROOT/wan.token"
echo "[Bees remote] Stage 3/5: fetching the current Bees worker runtime over the private tailnet..."
"$TAILNET_BRIDGE" fetch     --state "$TAILNET_STATE"     --hostname "$WORKER_HOSTNAME"     --target "$TAILNET_LEARNER:$TAILNET_BOOTSTRAP_PORT"     --token-file "$BOOTSTRAP_TOKEN_FILE"     --runtime-out "$RUNTIME_ZIP"     --worker-token-out "$WORKER_TOKEN"     --wan-token-out "$WAN_TOKEN"
chmod 600 "$WORKER_TOKEN" "$WAN_TOKEN"

echo "[Bees remote] Stage 4/5: preparing Python 3.10 worker environment..."
UV_BIN=""
if have uv; then
    UV_BIN="$(command -v uv)"
elif [[ -x "$HOME/.local/bin/uv" ]]; then
    UV_BIN="$HOME/.local/bin/uv"
else
    echo "[Bees remote] installing uv for an isolated Python 3.10 runtime..."
    if have curl; then
        curl -LsSf https://astral.sh/uv/install.sh | sh
    else
        wget -qO- https://astral.sh/uv/install.sh | sh
    fi
    if [[ -x "$HOME/.local/bin/uv" ]]; then
        UV_BIN="$HOME/.local/bin/uv"
    elif have uv; then
        UV_BIN="$(command -v uv)"
    else
        echo "error: uv installation completed but the executable could not be located." >&2
        exit 2
    fi
fi

echo "[Bees remote] ensuring Python 3.10 environment at $VENV_ROOT"
"$UV_BIN" python install 3.10
if [[ -x "$VENV_ROOT/bin/python" ]] && ! "$VENV_ROOT/bin/python" -c 'import sys; assert sys.version_info[:2] == (3, 10)' >/dev/null 2>&1; then
    rm -rf "$VENV_ROOT"
fi
if [[ ! -x "$VENV_ROOT/bin/python" ]]; then
    "$UV_BIN" venv --python 3.10 "$VENV_ROOT"
fi
VENV_PYTHON="$VENV_ROOT/bin/python"

rm -rf "$RUNTIME_ROOT"
mkdir -p "$RUNTIME_ROOT"
"$VENV_PYTHON" -m zipfile -e "$RUNTIME_ZIP" "$RUNTIME_ROOT"

REQUIREMENTS="$RUNTIME_ROOT/bees_remote_requirements.txt"
REQUIREMENTS_HASH="$("$VENV_PYTHON" - "$REQUIREMENTS" <<'PY'
import hashlib
import pathlib
import sys
print(hashlib.sha256(pathlib.Path(sys.argv[1]).read_bytes()).hexdigest())
PY
)"
REQUIREMENTS_STAMP="$VENV_ROOT/bees-requirements.sha256"
CURRENT_STAMP=""
if [[ -f "$REQUIREMENTS_STAMP" ]]; then
    CURRENT_STAMP="$(tr -d '\r\n' < "$REQUIREMENTS_STAMP")"
fi
DEPENDENCIES_OK=0
if "$VENV_PYTHON" -c 'import pkg_resources, mlagents, torch, numpy' >/dev/null 2>&1; then
    DEPENDENCIES_OK=1
fi
if [[ "$CURRENT_STAMP" != "$REQUIREMENTS_HASH" || "$DEPENDENCIES_OK" -ne 1 ]]; then
    echo "[Bees remote] installing/updating Python dependencies..."
    "$UV_BIN" pip install --python "$VENV_PYTHON" -r "$REQUIREMENTS"
    if ! "$VENV_PYTHON" -c 'import pkg_resources, mlagents, torch, numpy' >/dev/null 2>&1; then
        echo "error: remote Python dependency validation failed after installation." >&2
        exit 2
    fi
    printf '%s' "$REQUIREMENTS_HASH" > "$REQUIREMENTS_STAMP"
fi

WORKER="$RUNTIME_ROOT/bees_managed_remote_worker.py"
WORKER_ARGS=(
    "$WORKER"
    --tailnet-bridge "$TAILNET_BRIDGE"
    --tailnet-state "$TAILNET_STATE"
    --tailnet-hostname "$WORKER_HOSTNAME"
    --tailnet-target "$TAILNET_LEARNER"
    --control-port "$CONTROL_PORT"
    --bootstrap-port "$TAILNET_BOOTSTRAP_PORT"
    --broker-port "$BROKER_PORT"
    --gameplay-port "$GAMEPLAY_PORT"
    --install-root "$INSTALL_ROOT"
    --runtime-archive "$RUNTIME_ZIP"
    --launcher-path "$BEES_REMOTE_LAUNCHER_PATH"
    --bootstrap-token-file "$BOOTSTRAP_TOKEN_FILE"
    --worker-token-file "$WORKER_TOKEN"
    --wan-token-file "$WAN_TOKEN"
    --torch-device "$TORCH_DEVICE"
)
if [[ -n "$ENVS" ]]; then
    WORKER_ARGS+=(--envs "$ENVS")
fi
if (( NO_AUTOSTART )); then
    WORKER_ARGS+=(--no-autostart)
fi

echo
echo "[Bees remote] Stage 5/5: starting managed training worker in the background..."
if [[ -n "$ENVS" ]]; then
    echo "[Bees remote] starting worker with $ENVS environments."
else
    echo "[Bees remote] starting worker with BeesServer environment auto-optimization (CPU-derived start, RAM-capped maximum 64)."
fi
rm -f "$SHUTDOWN_REQUEST_FILE"
nohup "$VENV_PYTHON" -u "${WORKER_ARGS[@]}" >>"$SUPERVISOR_LOG" 2>&1 </dev/null &
WORKER_PID=$!
printf '%s' "$WORKER_PID" > "$SUPERVISOR_PID_FILE"
sleep 0.75
if ! kill -0 "$WORKER_PID" 2>/dev/null; then
    rm -f "$SUPERVISOR_PID_FILE"
    echo "error: remote worker exited during background startup. Check $SUPERVISOR_LOG." >&2
    exit 1
fi
install_remote_autostart
echo "[Bees remote] worker started in the background (PID $WORKER_PID)."
echo "[Bees remote] log: $SUPERVISOR_LOG"
echo "[Bees remote] close this shell freely; use 'bash bees-remote-worker.sh stop' to stop the worker."
exit 0
