#!/usr/bin/env bash
set -euo pipefail

DEFAULT_LEARNER="__BEES_LEARNER__"
DEFAULT_SSH_PORT="__BEES_SSH_PORT__"
DEFAULT_INSTALL_ROOT="__BEES_LINUX_INSTALL_ROOT__"
DEFAULT_TORCH_DEVICE="__BEES_TORCH_DEVICE__"
TAILNET_LEARNER="__BEES_TAILNET_LEARNER__"
TAILNET_PORT="__BEES_TAILNET_PORT__"
TAILNET_LOCAL_PORT="__BEES_TAILNET_LOCAL_PORT__"
TAILNET_BRIDGE_B64="__BEES_TAILNET_BRIDGE_B64__"
TAILNET_BRIDGE_SHA256="__BEES_TAILNET_BRIDGE_SHA256__"
REMOTE_RUNTIME_PATH="__BEES_RUNTIME_REMOTE_PATH__"
REMOTE_WORKER_TOKEN_PATH="__BEES_WORKER_TOKEN_REMOTE_PATH__"
REMOTE_WAN_TOKEN_PATH="__BEES_WAN_TOKEN_REMOTE_PATH__"

LEARNER="$DEFAULT_LEARNER"
ENVS=""
SSH_PORT="$DEFAULT_SSH_PORT"
INSTALL_ROOT="$DEFAULT_INSTALL_ROOT"
TORCH_DEVICE="$DEFAULT_TORCH_DEVICE"

usage() {
    cat <<'EOF'
Usage: bees-remote-worker.sh [options]

Options:
  --envs N               Unity environments on this machine (1-64).
  --install-root PATH    Local Linux worker installation directory.
  --torch-device DEVICE  Local inference device, normally cpu or cuda.
  -h, --help             Show this help.
EOF
}

while [[ $# -gt 0 ]]; do
    case "$1" in
        --envs) ENVS="$2"; shift 2 ;;
        --install-root) INSTALL_ROOT="$2"; shift 2 ;;
        --torch-device) TORCH_DEVICE="$2"; shift 2 ;;
        -h|--help) usage; exit 0 ;;
        *) echo "error: unknown argument: $1" >&2; usage >&2; exit 2 ;;
    esac
done

for value in "$LEARNER" "$SSH_PORT" "$INSTALL_ROOT" "$TORCH_DEVICE" "$TAILNET_LEARNER"     "$TAILNET_PORT" "$TAILNET_LOCAL_PORT" "$TAILNET_BRIDGE_B64" "$TAILNET_BRIDGE_SHA256"     "$REMOTE_RUNTIME_PATH" "$REMOTE_WORKER_TOKEN_PATH" "$REMOTE_WAN_TOKEN_PATH"; do
    if [[ "$value" == __BEES_* ]]; then
        echo "error: launcher is not configured. Copy the generated .sh file from the learner's B:\\Bees\\Remote directory after running bees.ps1 start." >&2
        exit 2
    fi
done

is_uint() { [[ "$1" =~ ^[0-9]+$ ]]; }
if [[ -n "$ENVS" ]] && { ! is_uint "$ENVS" || (( ENVS < 1 || ENVS > 64 )); }; then
    echo "error: --envs must be in 1-64 when specified" >&2
    exit 2
fi
for port in "$SSH_PORT" "$TAILNET_PORT" "$TAILNET_LOCAL_PORT"; do
    if ! is_uint "$port" || (( port < 1 || port > 65535 )); then
        echo "error: configured SSH/tailnet ports must be in 1-65535" >&2
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

sudo_cmd() {
    if [[ "$(id -u)" -eq 0 ]]; then
        "$@"
    elif have sudo; then
        sudo "$@"
    else
        echo "error: root privileges are required to install a missing prerequisite, but sudo is unavailable." >&2
        return 1
    fi
}

install_system_tools() {
    if have apt-get; then
        sudo_cmd apt-get update
        sudo_cmd apt-get install -y openssh-client curl ca-certificates coreutils
    elif have dnf; then
        sudo_cmd dnf install -y openssh-clients curl ca-certificates coreutils
    elif have yum; then
        sudo_cmd yum install -y openssh-clients curl ca-certificates coreutils
    elif have zypper; then
        sudo_cmd zypper --non-interactive install openssh curl ca-certificates coreutils
    elif have pacman; then
        sudo_cmd pacman -Sy --noconfirm openssh curl ca-certificates coreutils
    else
        echo "error: required base utilities are missing and no supported package manager was found." >&2
        return 1
    fi
}

if ! have base64 || { ! have sha256sum && ! have shasum; } || ! have ssh || ! have scp || { ! have curl && ! have wget; }; then
    echo "[Bees remote] installing missing base system prerequisites..."
    install_system_tools
fi
if ! have base64 || { ! have sha256sum && ! have shasum; }; then
    echo "error: base64 and SHA-256 utilities are required." >&2
    exit 2
fi
if ! have ssh || ! have scp; then
    echo "error: OpenSSH client is unavailable after prerequisite installation." >&2
    exit 2
fi

mkdir -p "$INSTALL_ROOT"
RUNTIME_ROOT="$INSTALL_ROOT/Runtime"
SECRETS_ROOT="$INSTALL_ROOT/Secrets"
DOWNLOADS_ROOT="$INSTALL_ROOT/Downloads"
VENV_ROOT="$INSTALL_ROOT/.venv"
TAILNET_ROOT="$INSTALL_ROOT/Tailnet"
TAILNET_STATE="$TAILNET_ROOT/State"
mkdir -p "$RUNTIME_ROOT" "$SECRETS_ROOT" "$DOWNLOADS_ROOT" "$TAILNET_ROOT" "$TAILNET_STATE"

hash_file() {
    if have sha256sum; then
        sha256sum "$1" | awk '{print $1}'
    else
        shasum -a 256 "$1" | awk '{print $1}'
    fi
}

TAILNET_BRIDGE="$TAILNET_ROOT/bees-tailnet-bridge"
WRITE_BRIDGE=1
if [[ -f "$TAILNET_BRIDGE" ]] && [[ "$(hash_file "$TAILNET_BRIDGE")" == "$TAILNET_BRIDGE_SHA256" ]]; then
    WRITE_BRIDGE=0
fi
if (( WRITE_BRIDGE )); then
    echo "[Bees remote] extracting bundled tailnet runtime..."
    printf '%s' "$TAILNET_BRIDGE_B64" | base64 -d > "$TAILNET_BRIDGE"
    chmod 700 "$TAILNET_BRIDGE"
fi
if [[ "$(hash_file "$TAILNET_BRIDGE")" != "$TAILNET_BRIDGE_SHA256" ]]; then
    echo "error: bundled tailnet runtime failed SHA-256 verification." >&2
    exit 2
fi

HOST_PART="$(hostname 2>/dev/null || printf 'linux')"
HOST_PART="${HOST_PART//[^A-Za-z0-9-]/-}"
HOST_PART="${HOST_PART,,}"
WORKER_HOSTNAME="bees-worker-$HOST_PART"

echo "[Bees remote] checking embedded tailnet identity."
echo "[Bees remote] on first use, open the Tailscale login URL printed below; no Tailscale installation is required."
"$TAILNET_BRIDGE" auth --state "$TAILNET_STATE" --hostname "$WORKER_HOSTNAME"

TAILNET_OUT="$TAILNET_ROOT/bridge.out.log"
TAILNET_ERR="$TAILNET_ROOT/bridge.err.log"
"$TAILNET_BRIDGE" forward     --state "$TAILNET_STATE"     --hostname "$WORKER_HOSTNAME"     --listen "127.0.0.1:$TAILNET_LOCAL_PORT"     --target "$TAILNET_LEARNER:$TAILNET_PORT"     >"$TAILNET_OUT" 2>"$TAILNET_ERR" &
TAILNET_PID=$!

cleanup_tailnet() {
    if kill -0 "$TAILNET_PID" >/dev/null 2>&1; then
        kill "$TAILNET_PID" >/dev/null 2>&1 || true
        wait "$TAILNET_PID" 2>/dev/null || true
    fi
}
trap cleanup_tailnet EXIT INT TERM

READY=0
for _ in $(seq 1 120); do
    if ! kill -0 "$TAILNET_PID" >/dev/null 2>&1; then
        echo "error: embedded tailnet bridge exited during startup. See $TAILNET_ERR" >&2
        exit 2
    fi
    if (exec 3<>"/dev/tcp/127.0.0.1/$TAILNET_LOCAL_PORT") 2>/dev/null; then
        exec 3>&- 3<&-
        READY=1
        break
    fi
    sleep 0.25
done
if (( ! READY )); then
    echo "error: embedded tailnet bridge did not open local port $TAILNET_LOCAL_PORT. See $TAILNET_ERR" >&2
    exit 2
fi
echo "[Bees remote] private tailnet path ready: 127.0.0.1:$TAILNET_LOCAL_PORT -> $TAILNET_LEARNER:$TAILNET_PORT"

RUNTIME_ZIP="$DOWNLOADS_ROOT/bees-remote-runtime.zip"
WORKER_TOKEN="$SECRETS_ROOT/training-worker.token"
WAN_TOKEN="$SECRETS_ROOT/wan.token"

SCP_ARGS=(-o StrictHostKeyChecking=accept-new)
if [[ "$SSH_PORT" != "22" ]]; then
    SCP_ARGS+=(-P "$SSH_PORT")
fi

copy_remote() {
    local remote_path="$1"
    local local_path="$2"
    echo "[Bees remote] fetching $remote_path"
    scp "${SCP_ARGS[@]}" "$LEARNER:$remote_path" "$local_path"
}

copy_remote "$REMOTE_RUNTIME_PATH" "$RUNTIME_ZIP"
copy_remote "$REMOTE_WORKER_TOKEN_PATH" "$WORKER_TOKEN"
copy_remote "$REMOTE_WAN_TOKEN_PATH" "$WAN_TOKEN"
chmod 600 "$WORKER_TOKEN" "$WAN_TOKEN"

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
if [[ "$CURRENT_STAMP" != "$REQUIREMENTS_HASH" ]]; then
    echo "[Bees remote] installing/updating Python dependencies..."
    "$UV_BIN" pip install --python "$VENV_PYTHON" -r "$REQUIREMENTS"
    printf '%s' "$REQUIREMENTS_HASH" > "$REQUIREMENTS_STAMP"
fi

WORKER="$RUNTIME_ROOT/bees_managed_remote_worker.py"
WORKER_ARGS=(
    "$WORKER"
    --learner "$LEARNER"
    --ssh-port "$SSH_PORT"
    --install-root "$INSTALL_ROOT"
    --worker-token-file "$WORKER_TOKEN"
    --wan-token-file "$WAN_TOKEN"
    --torch-device "$TORCH_DEVICE"
)
if [[ -n "$ENVS" ]]; then
    WORKER_ARGS+=(--envs "$ENVS")
fi

echo
if [[ -n "$ENVS" ]]; then
    echo "[Bees remote] starting worker with $ENVS environments."
else
    echo "[Bees remote] starting worker; environment count defaults to 4x available CPU threads (maximum 64)."
fi
echo "[Bees remote] the learner assigns the actor slot automatically. Ctrl+C stops this worker."
set +e
"$VENV_PYTHON" "${WORKER_ARGS[@]}"
EXIT_CODE=$?
set -e
exit "$EXIT_CODE"
