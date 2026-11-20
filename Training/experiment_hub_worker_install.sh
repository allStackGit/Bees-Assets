#!/usr/bin/env bash
# Install/update one trusted, CPU-only Experiment Hub worker for TinyBrains
# and/or Courier. Existing Bees workers and native Hub owners are untouched.
set -euo pipefail

MODE=install
PROJECTS=""
ROOT="${HOME}/ai/experiment-hub-worker"
MACHINE_ID=""
HUB_IP="100.97.43.98"
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
       [--root ABSOLUTE_PATH]
       [--machine-id EXISTING_HUB_MACHINE_ID]
       [--hub-tailnet-ip HALIFAX_TAILSCALE_IPV4]
       [--tinybrains-key EXISTING_PRIVATE_ROLLOUT_KEY]
       [--courier-key EXISTING_PRIVATE_HEX_KEY]
       [--tinybrains-root EXISTING_ANTS_MAPOCA_DIRECTORY]
       [--courier-root EXISTING_COURIER_DIRECTORY] [--start]

Run the file, follow the Tailscale sign-in link if prompted, and the worker
enrolls automatically. No SSH tunnel, GitHub password, copied secrets, Hub
registration form, or separate enrollment token.
'HELP'
}
if [[ ${1:-} == install || ${1:-} == update || ${1:-} == status ]]; then
    MODE=$1; shift
fi
while (( $# )); do
    case "$1" in
        --projects) echo "Simulation selection belongs to Experiment Hub, not the base installer." >&2; exit 2;;
        --root) ROOT=$2; shift 2;;
        --machine-id) MACHINE_ID=$2; shift 2;;
        --hub-tailnet-ip) HUB_IP=$2; shift 2;;
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
[[ "$MODE" != install || -z "$PROJECTS" ]] ||
    { echo "Install simulations separately through Experiment Hub." >&2; exit 2; }
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
# A one-file installation starts its agent and update timer automatically.
if [[ "$MODE" == install ]]; then START=1; fi
if [[ "$MODE" == install && -f "$HOME/.config/systemd/user/$SERVICE" ]]; then
    echo "An existing Hub agent already owns this Linux user. Do not install a second agent." >&2
    exit 1
fi
if [[ "$MODE" == install && -e "$ENVFILE" ]]; then
    echo "An agent is already installed here. Use 'update'; never overwrite its token." >&2
    exit 1
fi
if [[ "$MODE" == update && ! -f "$ENVFILE" ]]; then
    echo "No installed agent environment exists at $ENVFILE." >&2; exit 1
fi
if [[ "$MODE" == update ]] && systemctl --user is-active --quiet "$SERVICE"; then
    echo "Stop active Hub jobs through the dashboard, then stop the agent before updating." >&2
    exit 1
fi
if [[ "$MODE" == update ]]; then
    # Pinned project selection and installation paths belong to the
    # original enrolled worker, not new default CLI arguments.
    config_value() {
        local name=$1
        sed -n "s/^$name=//p" "$ENVFILE" | head -n 1
    }
    PROJECTS=$(config_value EXPERIMENT_HUB_WORKER_PROJECTS)
    TB_ROOT=$(config_value EXPERIMENT_HUB_TINYBRAINS_PROJECT_ROOT)
    COURIER_ROOT=$(config_value EXPERIMENT_HUB_COURIER_PROJECT_ROOT)

    if [[ -z "$HUB_IP" ]]; then
        PREVIOUS_URL=$(config_value EXPERIMENT_HUB_URL)
        if [[ "$PREVIOUS_URL" =~ ^http://(100\.[0-9]+\.[0-9]+\.[0-9]+):7149$ ]]; then
            HUB_IP="${BASH_REMATCH[1]}"
        fi
    fi
fi

install_dependencies() {
    if ! command -v apt-get >/dev/null; then
        echo "Automatic prerequisites currently support Debian/Ubuntu; install Node 18+ and Tailscale manually." >&2
        return 1
    fi
    sudo apt-get update
    sudo apt-get install -y curl ca-certificates nodejs
    if ! command -v tailscale >/dev/null; then
        curl -fsSL https://tailscale.com/install.sh | sudo sh
    fi
}
if [[ "$MODE" == install ]]; then install_dependencies; fi
for tool in node tailscale tar sha256sum; do
    command -v "$tool" >/dev/null || { echo "Missing prerequisite: $tool" >&2; exit 1; }
done
node -e 'if(Number(process.versions.node.split(".")[0])<18)process.exit(1)' ||
    { echo "Node.js 18 or newer is required." >&2; exit 1; }
# Python and uv are simulation dependencies, provisioned on-demand only.
umask 077
mkdir -p "$PRIVATE" "$SRC" "$ROOT/cache"
chmod 700 "$PRIVATE"
# The installer invokes Tailscale itself. The browser sign-in link is the
# only user action; a previously signed-in machine skips the login.
[[ "$HUB_IP" =~ ^100\.(6[4-9]|[7-9][0-9]|1[01][0-9]|12[0-7])\.[0-9]{1,3}\.[0-9]{1,3}$ ]] ||
    { echo "Halifax must be addressed by its private Tailscale IPv4." >&2; exit 1; }
TAILNET_IP=$(tailscale ip -4 2>/dev/null | head -n 1 || true)
if [[ ! "$TAILNET_IP" =~ ^100\. ]]; then
    echo "Sign in using the Tailscale URL below:"
    sudo tailscale up
    for attempt in $(seq 1 120); do
        TAILNET_IP=$(tailscale ip -4 2>/dev/null | head -n 1 || true)
        [[ "$TAILNET_IP" =~ ^100\. ]] && break
        sleep 2
    done
fi
[[ "$TAILNET_IP" =~ ^100\. ]] ||
    { echo "Tailscale authentication is required." >&2; exit 1; }
HUB_URL="http://$HUB_IP:7149"

# Bootstrap only on an owner-authenticated tailnet connection; no SSH or
# manual tokens. The returned rollout secrets are written with mode 0600.
if [[ "$MODE" == install ]]; then
    BOOT_BODY=$(node -e '
      const os=require("node:os");const projects=process.argv[1]?process.argv[1].split(","):[];
      process.stdout.write(JSON.stringify({projects,cpus:os.cpus().length,
        ram_mb:Math.floor(os.totalmem()/1048576)}));' "$PROJECTS")
    BOOT_REPLY=$(curl --noproxy '*' -fsS --max-time 30 -H 'Content-Type: application/json' \
        --data "$BOOT_BODY" "$HUB_URL/api/hub/agent/bootstrap")
    bootstrap_value() {
        printf '%s' "$BOOT_REPLY" | node -e '
          let data="";process.stdin.on("data",x=>data+=x);
          process.stdin.on("end",()=>{
            const r=JSON.parse(data),key=process.argv[1];
            const v=key==="machine_id"?r.machine_id:
              key==="agent_token"?r.agent_token:r.rollout_keys?.[key];
            if(typeof v!=="string"||!v)process.exitCode=1;
            else process.stdout.write(v);
          });' "$1"
    }
    MACHINE_ID=$(bootstrap_value machine_id)
    TOKEN=$(bootstrap_value agent_token)
    [[ "$MACHINE_ID" =~ ^[a-z][a-z0-9_-]{0,47}$ && "$TOKEN" =~ ^[0-9a-f]{64}$ ]] ||
        { echo "Invalid coordinator enrollment response." >&2; exit 1; }
    if has_project tinybrains; then bootstrap_value tinybrains > "$PRIVATE/tinybrains-rollout.key"; fi
    if has_project courier; then bootstrap_value courier > "$PRIVATE/courier-rollout.key"; fi
    chmod 600 "$PRIVATE/"*rollout.key
    unset BOOT_REPLY BOOT_BODY
fi

# Immutable SHA-256-checked archives come only from the Hub release publisher.
# Worker GitHub credentials are never needed.
install_release() {
    local component=$1 dest=$2 manifest version sha bytes archive actual stage
    manifest=$(curl --noproxy '*' -fsS --max-time 30 \
        -H "x-hub-agent-token: $TOKEN" \
        "$HUB_URL/api/hub/agent/releases?machine_id=$MACHINE_ID")
    read -r version sha bytes < <(printf '%s' "$manifest" | node -e '
      let data="";process.stdin.on("data",x=>data+=x);
      process.stdin.on("end",()=>{
        const v=JSON.parse(data).components?.[process.argv[1]];
        if(!v||!v.activation_ready||!/^[0-9a-f]{40}$/.test(v.version)||
           !/^[0-9a-f]{64}$/.test(v.sha256)||
           !Number.isSafeInteger(v.bytes)||v.bytes<1||v.bytes>536870912)
          process.exitCode=1;
        else process.stdout.write(v.version+" "+v.sha256+" "+v.bytes+"\n");
      });' "$component")
    [[ "$version" =~ ^[0-9a-f]{40}$ && "$sha" =~ ^[0-9a-f]{64}$ &&
       "$bytes" =~ ^[0-9]+$ ]] ||
        { echo "Published $component release is unavailable." >&2; return 1; }
    # An interrupted install may have already completed source extraction.
    # Reuse only a completed directory matching the approved release.
    if [[ -e "$dest" || -L "$dest" ]]; then
        if [[ -d "$dest" && ! -L "$dest" &&
              -f "$dest/.hub-release-version" &&
              "$(cat "$dest/.hub-release-version")" == "$version" ]]; then
            echo "Resuming $component from the previously installed release."
            return 0
        fi
        echo "Refusing to replace unexpected source directory: $dest" >&2
        return 1
    fi
    archive="$PRIVATE/$component-$$.tar"
    curl --noproxy '*' -fsS --max-time 180 \
        -H "x-hub-agent-token: $TOKEN" \
        "$HUB_URL/api/hub/agent/releases/bundle?machine_id=$MACHINE_ID&component=$component&sha256=$sha" \
        -o "$archive"
    actual=$(sha256sum "$archive" | cut -d' ' -f1)
    [[ "$actual" == "$sha" && "$(stat -c %s "$archive")" == "$bytes" ]] ||
        { rm -f "$archive"; echo "$component release checksum mismatch." >&2; return 1; }
    if tar -tvf "$archive" | grep -Eq '^[lh]' ||
        tar -tf "$archive" | grep -Eq '(^/|(^|/)\.\.(/|$)|\\|:)'; then
        rm -f "$archive"; echo "Unsafe release archive." >&2; return 1
    fi
    # Extract elsewhere, then publish the directory only after success.
    # Interrupted extractions cannot block the next one-file attempt.
    stage=$(mktemp -d "$SRC/.worker-release.XXXXXXXX")
    if ! tar -xf "$archive" -C "$stage"; then
        rm -rf -- "$stage"
        rm -f -- "$archive"
        echo "Failed to extract $component software release." >&2
        return 1
    fi
    printf '%s\n' "$version" > "$stage/.hub-release-version"
    chmod 600 "$stage/.hub-release-version"
    if ! mv -- "$stage" "$dest"; then
        rm -rf -- "$stage"
        rm -f -- "$archive"
        return 1
    fi
    rm -f "$archive"
}
if [[ "$MODE" == install ]]; then
    install_release worker "$SRC/Bees-Assets"
fi
if [[ "$MODE" == install ]]; then
    [[ "$MACHINE_ID" =~ ^[a-z][a-z0-9_-]{0,47}$ && "$TOKEN" =~ ^[0-9a-f]{64}$ ]] ||
        { echo "Tailscale bootstrap did not return valid enrollment credentials." >&2; exit 1; }
    {
        printf 'EXPERIMENT_HUB_MACHINE_ID=%s\n' "$MACHINE_ID"
        printf 'EXPERIMENT_HUB_AGENT_TOKEN=%s\n' "$TOKEN"
        printf 'EXPERIMENT_HUB_ENABLE_EXECUTION=1\n'
        printf 'EXPERIMENT_HUB_LOCAL_ADAPTERS=%s\n' "$TRAINING/experiment_hub_worker_adapters.js"
        printf 'EXPERIMENT_HUB_LOCAL_ROOT=%s\n' "$ROOT/native-state"
        printf 'EXPERIMENT_HUB_URL=http://%s:7149\n' "$HUB_IP"
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
else
    # Safe in-place upgrade: retain enrollment and native project identity,
    # replace only transport configuration and discard retired SSH settings.
    TEMP_ENV="$ENVFILE.tailnet.$$"
    awk -v ip="$HUB_IP" '
        /^EXPERIMENT_HUB_SSH_/ {next}
        /^EXPERIMENT_HUB_URL=/ {print "EXPERIMENT_HUB_URL=http://" ip ":7149"; found=1; next}
        {print}
        END {if(!found) print "EXPERIMENT_HUB_URL=http://" ip ":7149"}
    ' "$ENVFILE" > "$TEMP_ENV"
    chmod 600 "$TEMP_ENV"
    mv -f "$TEMP_ENV" "$ENVFILE"
fi
mkdir -p "$ROOT/native-state"
chmod 700 "$ROOT/native-state"
if [[ ! -f "$HOME/.config/systemd/user/$SERVICE" ]]; then
    node "$TRAINING/experiment_hub_systemd.js" agent --training-dir "$TRAINING" \
        --env-file "$ENVFILE" --install
fi
systemctl --user daemon-reload
if (( START )); then
    echo "Starting enrolled Hub agent."
    if systemctl --user is-active --quiet "$SERVICE"; then
        echo "Existing agent still running; stop managed jobs and restart it through the Hub." >&2
    else
        systemctl --user enable "$SERVICE"
        systemctl --user start "$SERVICE"
    fi
fi
# A separate user-systemd timer survives agent restarts. It contacts only
# Halifax's authenticated tailnet release API, never the private GitHub repos.
UPDATER_DIR="$ROOT/Update"
UPDATER_UNIT="$HOME/.config/systemd/user/bees-experiment-hub-worker-update.service"
UPDATER_TIMER="$HOME/.config/systemd/user/bees-experiment-hub-worker-update.timer"
mkdir -p "$UPDATER_DIR"
install -m 600 "$TRAINING/experiment_hub_worker_updater.js" \
    "$UPDATER_DIR/experiment_hub_worker_updater.js"
install -m 600 "$TRAINING/experiment_hub_worker_update_bootstrap.js" \
    "$UPDATER_DIR/experiment_hub_worker_update_bootstrap.js"
node --check "$UPDATER_DIR/experiment_hub_worker_update_bootstrap.js"
NODE_BIN=$(command -v node)
[[ "$ROOT" =~ ^/[A-Za-z0-9._/@:+-]+$ && "$NODE_BIN" =~ ^/[A-Za-z0-9._/@:+-]+$ ]] || {
    echo "Updater systemd paths contain unsupported characters." >&2
    exit 1
}
cat > "$UPDATER_UNIT" <<EOF
[Unit]
Description=Experiment Hub authenticated worker release updates
After=network.target
[Service]
Type=oneshot
ExecStart=$NODE_BIN $UPDATER_DIR/experiment_hub_worker_update_bootstrap.js --root $ROOT
WorkingDirectory=$ROOT
UMask=0077
EOF
cat > "$UPDATER_TIMER" <<EOF
[Unit]
Description=Poll approved Experiment Hub software releases
[Timer]
OnBootSec=2min
OnUnitInactiveSec=2min
Persistent=true
Unit=bees-experiment-hub-worker-update.service
[Install]
WantedBy=timers.target
EOF
chmod 600 "$UPDATER_UNIT" "$UPDATER_TIMER"
# Persist the user service manager on headless Linux worker machines;
# otherwise neither agent nor update timer runs until an interactive login.
if command -v loginctl >/dev/null 2>&1; then
    sudo loginctl enable-linger "$(id -un)"
fi
systemctl --user daemon-reload
systemctl --user enable --now bees-experiment-hub-worker-update.timer

echo "Worker prepared. No native experiment was started."
echo "Project runtimes are installed separately by Experiment Hub when assigned."
echo "Enrolled through Tailscale; worker will report its capabilities to Hub Machines."
