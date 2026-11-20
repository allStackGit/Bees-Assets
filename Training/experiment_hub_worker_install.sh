#!/usr/bin/env bash
# Install/update one trusted, CPU-only Experiment Hub worker for TinyBrains
# and/or Courier. Existing Bees workers and native Hub owners are untouched.
set -euo pipefail

MODE=install
PROJECTS=tinybrains,courier
ROOT="${HOME}/ai/experiment-hub-worker"
MACHINE_ID=""
HUB_TARGET=""
START=0
TB_KEY=""
COURIER_KEY=""
TB_ROOT=""
COURIER_ROOT=""
ASSETS_BRANCH=feat/hub-resource-experiments-20261110
TB_BRANCH=feat/linux-tinybrains-runtime
COURIER_BRANCH=feat/hub-independent-courier-ppo-20261119

usage() {
    cat <<'HELP'
Usage: bash Training/experiment_hub_worker_install.sh [install|update|status]
       [--projects tinybrains,courier] [--root ABSOLUTE_PATH]
       [--machine-id EXISTING_HUB_MACHINE_ID]
       [--hub-ssh-target USER@HALIFAX_TAILSCALE_IPV4]
       [--tinybrains-key EXISTING_PRIVATE_ROLLOUT_KEY]
       [--courier-key EXISTING_PRIVATE_HEX_KEY]
       [--tinybrains-root EXISTING_ANTS_MAPOCA_DIRECTORY]
       [--courier-root EXISTING_COURIER_DIRECTORY] [--start]

First enroll ONE machine in the Hub Machines tab and keep its one-time token
private; the installer requests it without echo. SSH to Halifax and its
private agent-only port 7149 are required for a remote machine.
'HELP'
}
if [[ ${1:-} == install || ${1:-} == update || ${1:-} == status ]]; then
    MODE=$1; shift
fi
while (( $# )); do
    case "$1" in
        --projects) PROJECTS=$2; shift 2;;
        --root) ROOT=$2; shift 2;;
        --machine-id) MACHINE_ID=$2; shift 2;;
        --hub-ssh-target) HUB_TARGET=$2; shift 2;;
        --tinybrains-key) TB_KEY=$2; shift 2;;
        --courier-key) COURIER_KEY=$2; shift 2;;
        --tinybrains-root) TB_ROOT=$2; shift 2;;
        --courier-root) COURIER_ROOT=$2; shift 2;;
        --start) START=1; shift;;
        -h|--help) usage; exit 0;;
        *) usage >&2; exit 2;;
    esac
done

[[ "$ROOT" == /* && "$ROOT" != / && "$ROOT" != */../* ]] ||
    { echo "Use a dedicated absolute installation root." >&2; exit 2; }
[[ "$PROJECTS" == tinybrains || "$PROJECTS" == courier ||
    "$PROJECTS" == tinybrains,courier || "$PROJECTS" == courier,tinybrains ]] ||
    { echo "Select tinybrains, courier or both." >&2; exit 2; }
has_project() { [[ ",$PROJECTS," == *",$1,"* ]]; }
PRIVATE="$ROOT/private"
SRC="$ROOT/sources"
VENV="$ROOT/venvs"
ENVFILE="$PRIVATE/worker.env"
TRAINING="$SRC/Bees-Assets/Training"
SERVICE=bees-experiment-hub-agent.service

if [[ "$MODE" == status ]]; then
    systemctl --user status "$SERVICE" --no-pager || true
    command -v tailscale >/dev/null && tailscale status || true
    echo "Agent environment: $ENVFILE (private; contents suppressed)"
    exit 0
fi
if [[ "$MODE" == install && -e "$ENVFILE" ]]; then
    echo "An agent is already installed here. Use 'update'; never overwrite its token." >&2
    exit 1
fi
if [[ "$MODE" == update && ! -f "$ENVFILE" ]]; then
    echo "No installed agent environment exists at $ENVFILE." >&2; exit 1
fi

install_dependencies() {
    if ! command -v apt-get >/dev/null; then
        echo "Automatic prerequisites currently support Debian/Ubuntu; install Node 18+, Python 3.11+, Git, SSH and Tailscale manually." >&2
        return 1
    fi
    sudo apt-get update
    sudo apt-get install -y git curl openssh-client python3 python3-venv python3-pip nodejs
    if ! command -v tailscale >/dev/null; then
        curl -fsSL https://tailscale.com/install.sh | sudo sh
    fi
}
if [[ "$MODE" == install ]]; then install_dependencies; fi
for tool in git node python3 ssh ssh-keygen ssh-keyscan; do
    command -v "$tool" >/dev/null || { echo "Missing prerequisite: $tool" >&2; exit 1; }
done
node -e 'if(Number(process.versions.node.split(".")[0])<18)process.exit(1)' ||
    { echo "Node.js 18 or newer is required." >&2; exit 1; }
python3 -c 'import sys; assert sys.version_info >= (3,11)' ||
    { echo "Python 3.11 or newer is required." >&2; exit 1; }
umask 077
mkdir -p "$PRIVATE" "$SRC" "$VENV" "$ROOT/cache/tinybrains" "$ROOT/cache/courier"
chmod 700 "$PRIVATE"
for private in "$TB_KEY" "$COURIER_KEY"; do
    [[ -z "$private" || -f "$private" ]] ||
        { echo "Approved private rollout key file is missing." >&2; exit 1; }
done

# Private repositories require the operator's GitHub credential. Never
# reset branches, remove local changes, or run an unreviewed remote script.
sync_checkout() {
    local url=$1 branch=$2 dir=$3
    if [[ ! -d "$dir/.git" ]]; then
        git clone --filter=blob:none --depth=1 --branch "$branch" "$url" "$dir"
    else
        [[ -z "$(git -C "$dir" status --porcelain)" ]] ||
            { echo "Refusing to change a dirty checkout: $dir" >&2; exit 1; }
        [[ "$(git -C "$dir" branch --show-current)" == "$branch" ]] ||
            { echo "Unexpected source branch at $dir" >&2; exit 1; }
        git -C "$dir" pull --ff-only origin "$branch"
    fi
}
sync_checkout https://github.com/allStackGit/Bees-Assets.git "$ASSETS_BRANCH" "$SRC/Bees-Assets"
if has_project tinybrains && [[ -z "$TB_ROOT" ]]; then
    sync_checkout https://github.com/allStackGit/TinyBrains.git "$TB_BRANCH" "$SRC/TinyBrains"
    TB_ROOT="$SRC/TinyBrains/Ants-MAPOCA"
fi
if has_project courier && [[ -z "$COURIER_ROOT" ]]; then
    sync_checkout https://github.com/allStackGit/Basic-Training.git "$COURIER_BRANCH" "$SRC/Basic-Training"
    COURIER_ROOT="$SRC/Basic-Training"
fi

# Keep conflicting TinyBrains/Courier Torch versions in separate venvs.
if has_project tinybrains; then
    [[ -f "$TB_ROOT/pyproject.toml" ]] || { echo "TinyBrains source is missing." >&2; exit 1; }
    python3 -m venv "$VENV/tinybrains"
    "$VENV/tinybrains/bin/python" -m pip install --disable-pip-version-check --index-url https://download.pytorch.org/whl/cpu 'torch==2.6.0'
    "$VENV/tinybrains/bin/python" -m pip install --disable-pip-version-check -e "$TB_ROOT"
    "$VENV/tinybrains/bin/python" -B -c 'import ants_mapoca.remote_rollout'
fi
if has_project courier; then
    [[ -f "$COURIER_ROOT/requirements.txt" ]] || { echo "Courier source is missing." >&2; exit 1; }
    python3 -m venv "$VENV/courier"
    "$VENV/courier/bin/python" -m pip install --disable-pip-version-check -r "$COURIER_ROOT/requirements.txt"
    "$VENV/courier/bin/python" -B -c 'import courier_remote_lineage' 2>/dev/null ||
        (cd "$COURIER_ROOT" && "$VENV/courier/bin/python" -B -c 'import courier_remote_lineage')
fi

if [[ "$MODE" == install ]]; then
    [[ "$MACHINE_ID" =~ ^[a-z][a-z0-9_-]{0,47}$ ]] ||
        { echo "Supply the exact enrolled Hub machine ID via --machine-id." >&2; exit 1; }
    [[ "$HUB_TARGET" =~ ^[A-Za-z_][A-Za-z0-9_.-]*@100\. ]] ||
        { echo "Supply user@Halifax-tailnet-IP via --hub-ssh-target." >&2; exit 1; }
    [[ -n "$TB_KEY" || "$PROJECTS" != *tinybrains* ]] ||
        { echo "Supply the same private TinyBrains rollout key used by the Halifax learner." >&2; exit 1; }
    if has_project courier && [[ -z "$COURIER_KEY" ]]; then
        echo "Supply the same private Courier remote key used by the learner." >&2; exit 1
    fi
    read -r -s -p 'One-time Hub agent enrollment token (64 lowercase hex characters): ' TOKEN
    echo
    [[ "$TOKEN" =~ ^[0-9a-f]{64}$ ]] ||
        { echo "Invalid token; enroll the machine in the Hub first." >&2; exit 1; }
    [[ -n "$HUB_TARGET" ]] || exit 1
    if [[ ! -f "$PRIVATE/ssh_ed25519" ]]; then
        ssh-keygen -q -t ed25519 -N '' -f "$PRIVATE/ssh_ed25519"
    fi
    if [[ ! -s "$PRIVATE/known_hosts" ]]; then
        host="${HUB_TARGET#*@}"
        ssh-keyscan -T 10 -t ed25519 "$host" > "$PRIVATE/known_hosts.candidate" 2>/dev/null
        [[ -s "$PRIVATE/known_hosts.candidate" ]] ||
            { echo "Cannot read Halifax SSH host key. Enable SSH over the tailnet first." >&2; exit 1; }
        echo "Verify this fingerprint against 'ssh-keygen -lf /etc/ssh/ssh_host_ed25519_key.pub' on Halifax:"
        ssh-keygen -lf "$PRIVATE/known_hosts.candidate"
        read -r -p 'Type VERIFIED after comparing the fingerprint on Halifax: ' CONFIRM
        [[ "$CONFIRM" == VERIFIED ]] || { rm -f "$PRIVATE/known_hosts.candidate"; exit 1; }
        mv "$PRIVATE/known_hosts.candidate" "$PRIVATE/known_hosts"
    fi
    echo "Authorizing this worker's SSH public key with the Halifax user (may request SSH password)..."
    ssh-copy-id -i "$PRIVATE/ssh_ed25519.pub" -o "UserKnownHostsFile=$PRIVATE/known_hosts" \
        -o StrictHostKeyChecking=yes "$HUB_TARGET"
    ssh -o BatchMode=yes -o "UserKnownHostsFile=$PRIVATE/known_hosts" \
        -o StrictHostKeyChecking=yes -i "$PRIVATE/ssh_ed25519" "$HUB_TARGET" true
    TAILNET_IP=$(tailscale ip -4 2>/dev/null | head -n 1)
    [[ "$TAILNET_IP" =~ ^100\. ]] ||
        { echo "Sign this machine into Tailscale first; then rerun setup." >&2; exit 1; }
    if has_project tinybrains; then install -m 600 "$TB_KEY" "$PRIVATE/tinybrains-rollout.key"; fi
    if has_project courier; then install -m 600 "$COURIER_KEY" "$PRIVATE/courier-rollout.key"; fi
    {
        printf 'EXPERIMENT_HUB_MACHINE_ID=%s\n' "$MACHINE_ID"
        printf 'EXPERIMENT_HUB_AGENT_TOKEN=%s\n' "$TOKEN"
        printf 'EXPERIMENT_HUB_ENABLE_EXECUTION=1\n'
        printf 'EXPERIMENT_HUB_LOCAL_ADAPTERS=%s\n' "$TRAINING/experiment_hub_worker_adapters.js"
        printf 'EXPERIMENT_HUB_LOCAL_ROOT=%s\n' "$ROOT/native-state"
        printf 'EXPERIMENT_HUB_URL=http://127.0.0.1:17149\n'
        printf 'EXPERIMENT_HUB_SSH_TARGET=%s\n' "$HUB_TARGET"
        printf 'EXPERIMENT_HUB_SSH_KEY=%s\n' "$PRIVATE/ssh_ed25519"
        printf 'EXPERIMENT_HUB_SSH_KNOWN_HOSTS=%s\n' "$PRIVATE/known_hosts"
        printf 'EXPERIMENT_HUB_TAILNET_IP=%s\n' "$TAILNET_IP"
        printf 'EXPERIMENT_HUB_WORKER_PROJECTS=%s\n' "$PROJECTS"
        if has_project tinybrains; then
            printf 'EXPERIMENT_HUB_TINYBRAINS_PROJECT_ROOT=%s\n' "$TB_ROOT"
            printf 'EXPERIMENT_HUB_TINYBRAINS_PYTHON=%s\n' "$VENV/tinybrains/bin/python"
            printf 'EXPERIMENT_HUB_TINYBRAINS_CACHE=%s\n' "$ROOT/cache/tinybrains"
            printf 'EXPERIMENT_HUB_TINYBRAINS_ROLLOUT_KEY=%s\n' "$PRIVATE/tinybrains-rollout.key"
            printf 'EXPERIMENT_HUB_TINYBRAINS_THREAD_LIMIT=%s\n' "$(nproc)"
        fi
        if has_project courier; then
            printf 'EXPERIMENT_HUB_COURIER_PROJECT_ROOT=%s\n' "$COURIER_ROOT"
            printf 'EXPERIMENT_HUB_COURIER_PYTHON=%s\n' "$VENV/courier/bin/python"
            printf 'EXPERIMENT_HUB_COURIER_CACHE=%s\n' "$ROOT/cache/courier"
            printf 'EXPERIMENT_HUB_COURIER_ROLLOUT_KEY=%s\n' "$PRIVATE/courier-rollout.key"
        fi
    } > "$ENVFILE"
    chmod 600 "$ENVFILE"
fi
mkdir -p "$ROOT/native-state"
chmod 700 "$ROOT/native-state"
if [[ ! -f "$HOME/.config/systemd/user/$SERVICE" ]]; then
    node "$TRAINING/experiment_hub_systemd.js" agent --training-dir "$TRAINING" \
        --env-file "$ENVFILE" --install
fi
systemctl --user daemon-reload
if (( START )); then
    echo "Start requested: confirm there are no active native jobs before restarting an existing agent."
    if systemctl --user is-active --quiet "$SERVICE"; then
        echo "Existing agent still running; stop managed jobs and restart it through the Hub." >&2
    else
        systemctl --user start "$SERVICE"
    fi
fi
echo "Worker prepared. No native experiment was started."
echo "Tailscale listeners: TinyBrains 12134/12135; Courier 13134/13135 (enabled projects only)."
echo "In Hub Machines, register/enroll this exact worker and confirm its authenticated native capability report."
