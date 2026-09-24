#!/usr/bin/env bash
set -euo pipefail

DEFAULT_LEARNER="__BEES_LEARNER__"
DEFAULT_ACTOR_ID="__BEES_ACTOR_ID__"
DEFAULT_ENVS="__BEES_ENVS__"
DEFAULT_SSH_PORT="__BEES_SSH_PORT__"
DEFAULT_INSTALL_ROOT="__BEES_LINUX_INSTALL_ROOT__"
DEFAULT_TORCH_DEVICE="__BEES_TORCH_DEVICE__"
REMOTE_RUNTIME_PATH="__BEES_RUNTIME_REMOTE_PATH__"
REMOTE_WORKER_TOKEN_PATH="__BEES_WORKER_TOKEN_REMOTE_PATH__"
REMOTE_WAN_TOKEN_PATH="__BEES_WAN_TOKEN_REMOTE_PATH__"

LEARNER="$DEFAULT_LEARNER"
ACTOR_ID="$DEFAULT_ACTOR_ID"
ENVS="$DEFAULT_ENVS"
SSH_PORT="$DEFAULT_SSH_PORT"
INSTALL_ROOT="$DEFAULT_INSTALL_ROOT"
TORCH_DEVICE="$DEFAULT_TORCH_DEVICE"

usage() {
    cat <<'EOF'
Usage: bees-remote-worker-N.sh [options]

Options:
  --learner TARGET       SSH target for the central learner.
  --actor-id N           Elastic actor slot (0-11).
  --envs N               Unity environments on this machine (1-64).
  --ssh-port N           SSH port for the learner.
  --install-root PATH    Local Linux worker installation directory.
  --torch-device DEVICE  Local inference device, normally cpu or cuda.
  -h, --help             Show this help.
EOF
}

while [[ $# -gt 0 ]]; do
    case "$1" in
        --learner) LEARNER="$2"; shift 2 ;;
        --actor-id) ACTOR_ID="$2"; shift 2 ;;
        --envs) ENVS="$2"; shift 2 ;;
        --ssh-port) SSH_PORT="$2"; shift 2 ;;
        --install-root) INSTALL_ROOT="$2"; shift 2 ;;
        --torch-device) TORCH_DEVICE="$2"; shift 2 ;;
        -h|--help) usage; exit 0 ;;
        *) echo "error: unknown argument: $1" >&2; usage >&2; exit 2 ;;
    esac
done

for value in "$LEARNER" "$ACTOR_ID" "$ENVS" "$SSH_PORT" "$INSTALL_ROOT" "$TORCH_DEVICE" \
    "$REMOTE_RUNTIME_PATH" "$REMOTE_WORKER_TOKEN_PATH" "$REMOTE_WAN_TOKEN_PATH"; do
    if [[ "$value" == __BEES_* ]]; then
        echo "error: launcher is not configured. Copy a generated .sh file from the learner's B:\\Bees\\Remote directory after running bees.ps1 start." >&2
        exit 2
    fi
done

is_uint() { [[ "$1" =~ ^[0-9]+$ ]]; }
if ! is_uint "$ACTOR_ID" || (( ACTOR_ID < 0 || ACTOR_ID > 11 )); then
    echo "error: --actor-id must be in 0-11" >&2
    exit 2
fi
if ! is_uint "$ENVS" || (( ENVS < 1 || ENVS > 64 )); then
    echo "error: --envs must be in 1-64" >&2
    exit 2
fi
if ! is_uint "$SSH_PORT" || (( SSH_PORT < 1 || SSH_PORT > 65535 )); then
    echo "error: --ssh-port must be in 1-65535" >&2
    exit 2
fi

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
        echo "error: root privileges are required to install missing system packages, but sudo is unavailable." >&2
        return 1
    fi
}

install_system_tools() {
    if have apt-get; then
        sudo_cmd apt-get update
        sudo_cmd apt-get install -y openssh-client curl ca-certificates
    elif have dnf; then
        sudo_cmd dnf install -y openssh-clients curl ca-certificates
    elif have yum; then
        sudo_cmd yum install -y openssh-clients curl ca-certificates
    elif have zypper; then
        sudo_cmd zypper --non-interactive install openssh curl ca-certificates
    elif have pacman; then
        sudo_cmd pacman -Sy --noconfirm openssh curl ca-certificates
    else
        echo "error: ssh/scp/curl are required and no supported package manager was found." >&2
        return 1
    fi
}

if ! have ssh || ! have scp || { ! have curl && ! have wget; }; then
    echo "[Bees remote] installing missing SSH/download prerequisites..."
    install_system_tools
fi
if ! have ssh || ! have scp; then
    echo "error: OpenSSH client is still unavailable after prerequisite installation." >&2
    exit 2
fi
if ! have curl && ! have wget; then
    echo "error: curl or wget is required to bootstrap Python." >&2
    exit 2
fi

mkdir -p "$INSTALL_ROOT"
RUNTIME_ROOT="$INSTALL_ROOT/Runtime"
SECRETS_ROOT="$INSTALL_ROOT/Secrets"
DOWNLOADS_ROOT="$INSTALL_ROOT/Downloads"
VENV_ROOT="$INSTALL_ROOT/.venv"
mkdir -p "$RUNTIME_ROOT" "$SECRETS_ROOT" "$DOWNLOADS_ROOT"

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
echo
echo "[Bees remote] starting actor $ACTOR_ID with $ENVS environments via $LEARNER."
echo "[Bees remote] leave this process running; Ctrl+C stops this worker."
exec "$VENV_PYTHON" "$WORKER" \
    --learner "$LEARNER" \
    --ssh-port "$SSH_PORT" \
    --actor-id "$ACTOR_ID" \
    --envs "$ENVS" \
    --install-root "$INSTALL_ROOT" \
    --worker-token-file "$WORKER_TOKEN" \
    --wan-token-file "$WAN_TOKEN" \
    --torch-device "$TORCH_DEVICE"
