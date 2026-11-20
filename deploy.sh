#!/usr/bin/env bash
# Deploy the digital twin to the lab host: update the code there from GitHub,
# copy the WebGL build (and, the first time, the database settings), then
# rebuild and restart the containers. Run from this repo on a machine that has
# the WebGL build in deploy/webgl/.
#
#   ./deploy.sh              normal deploy
#   ./deploy.sh --no-webgl   skip copying the WebGL build (bridge-only change)
#   ./deploy.sh --env        also overwrite the lab host's vt_middleware/.env
#                            (normally only copied if it isn't there yet)
#
# Settings (environment variables, defaults in brackets):
#   LAB_HOST    [ubuntu@192.168.2.100]  ssh login for the lab host
#   REMOTE_DIR  [digital-twin]          install folder there, relative to its home
#   WEB_PORT    [8080]                  port the site is served on; only written to
#                                       the lab host's .env the first time (80 is
#                                       taken by the AMS dashboard there)
#
# The lab host pulls the code from GitHub, so push your commits first; the
# script warns if you haven't. The WebGL build and .env are copied from here
# because they're not in git (licensed meshes, database password).

set -euo pipefail

LAB_HOST="${LAB_HOST:-ubuntu@192.168.2.100}"
REMOTE_DIR="${REMOTE_DIR:-digital-twin}"
WEB_PORT="${WEB_PORT:-8080}"
REPO_URL="https://github.com/calebrpeterson02/smr-lab-digital-twin.git"

copy_webgl=1
overwrite_env=0
for arg in "$@"; do
    case "$arg" in
        --no-webgl) copy_webgl=0 ;;
        --env)      overwrite_env=1 ;;
        -h|--help)  sed -n '2,/^$/p' "$0" | sed 's/^# \{0,1\}//'; exit 0 ;;
        *) echo "Unknown option: $arg (see --help)" >&2; exit 2 ;;
    esac
done

# Run from the repo root whatever directory the script was started from
cd "$(dirname "$0")"

step() { printf '\n==> %s\n' "$1"; }
die()  { printf '\n!! %s\n' "$1" >&2; exit 1; }

# One ssh connection, reused by every ssh/rsync/scp below, so a password is
# asked for once rather than at every step. Closed again when the script exits.
SSH_SOCKET="$(mktemp -u /tmp/deploy-ssh-XXXXXX)" # short path: ssh limits socket paths to ~100 chars
SSH_OPTS=(-o ControlMaster=auto -o "ControlPath=$SSH_SOCKET" -o ControlPersist=120)
cleanup() { ssh "${SSH_OPTS[@]}" -O exit "$LAB_HOST" 2>/dev/null || true; }
trap cleanup EXIT
remote() { ssh "${SSH_OPTS[@]}" "$LAB_HOST" "$@"; }

# --- Checks on this machine -------------------------------------------------
step "Checking this machine"
[ -f docker-compose.yml ] || die "Run this from the repo (docker-compose.yml not found)."
if [ "$copy_webgl" = 1 ] && [ ! -f deploy/webgl/index.html ]; then
    die "No WebGL build in deploy/webgl/. Build it from Unity first (see README), or use --no-webgl."
fi

# The lab host gets the code from GitHub, so anything not pushed won't be deployed
if [ -n "$(git status --porcelain --untracked-files=no)" ]; then
    echo "Warning: you have uncommitted changes. They won't be deployed (the lab host pulls from GitHub)."
fi
git fetch --quiet origin main 2>/dev/null || echo "Warning: couldn't reach GitHub to compare with origin/main."
if [ "$(git rev-parse HEAD)" != "$(git rev-parse origin/main 2>/dev/null)" ]; then
    echo "Warning: this checkout isn't the same as origin/main. The lab host will get origin/main."
    echo "         ($(git log --oneline -1 origin/main 2>/dev/null))"
fi

# --- Code on the lab host ---------------------------------------------------
step "Connecting to $LAB_HOST"
remote true || die "Couldn't ssh to $LAB_HOST."

step "Updating the code on the lab host (~/$REMOTE_DIR)"
# Clones on the first deploy; after that only fast-forwards, so it stops rather
# than merging if someone changed files on the lab host by hand.
remote bash -s -- "$REMOTE_DIR" "$REPO_URL" "$WEB_PORT" <<'EOF'
set -euo pipefail
dir="$1"; url="$2"; port="$3"
if [ -d "$dir/.git" ]; then
    git -C "$dir" pull --ff-only --quiet
else
    git clone --quiet "$url" "$dir"
    echo "Cloned into ~/$dir"
fi
git -C "$dir" log --oneline -1

# Compose reads WEB_PORT from this file. Written once; edit it there to change the port.
if [ ! -f "$dir/.env" ]; then
    echo "WEB_PORT=$port" > "$dir/.env"
    echo "Created ~/$dir/.env with WEB_PORT=$port"
fi
EOF

# --- Files that aren't in git -----------------------------------------------
step "Database settings (vt_middleware/.env)"
if [ "$overwrite_env" = 1 ] || ! remote test -f "$REMOTE_DIR/vt_middleware/.env"; then
    [ -f vt_middleware/.env ] || die "vt_middleware/.env isn't on this machine either - create it first (see README)."
    scp -q "${SSH_OPTS[@]}" vt_middleware/.env "$LAB_HOST:$REMOTE_DIR/vt_middleware/.env"
    remote chmod 600 "$REMOTE_DIR/vt_middleware/.env" # holds the database password
    echo "Copied."
else
    echo "Already on the lab host; left as is (use --env to overwrite)."
fi

if [ "$copy_webgl" = 1 ]; then
    step "Copying the WebGL build"
    remote mkdir -p "$REMOTE_DIR/deploy/webgl"
    if command -v rsync >/dev/null && remote command -v rsync >/dev/null; then
        # --delete removes files left over from an older build
        rsync -az --delete --info=stats1 -e "ssh ${SSH_OPTS[*]}" deploy/webgl/ "$LAB_HOST:$REMOTE_DIR/deploy/webgl/"
    else
        # No rsync on one side: send the whole build as a tar stream instead
        tar -C deploy/webgl -cz . | remote "rm -rf '$REMOTE_DIR/deploy/webgl'/* && tar -C '$REMOTE_DIR/deploy/webgl' -xz"
        echo "Copied (tar)."
    fi
fi

# --- Restart ----------------------------------------------------------------
step "Rebuilding and restarting the containers"
# --build only rebuilds the bridges image if its files changed. nginx serves the
# mounted WebGL build directly, so a new build needs no restart.
remote "cd '$REMOTE_DIR' && docker compose up -d --build --remove-orphans && docker compose ps"

port="$(remote "sed -n 's/^WEB_PORT=//p' '$REMOTE_DIR/.env'" | tail -1)"
step "Done: http://${LAB_HOST#*@}:${port:-80}/"
echo "Logs: ssh $LAB_HOST 'cd $REMOTE_DIR && docker compose logs -f'"
