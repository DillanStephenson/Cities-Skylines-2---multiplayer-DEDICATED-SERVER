#!/usr/bin/env bash
# Installs or updates the server window on an Ubuntu box as a hardened systemd service.
# Usage (as root):  setup-vps.sh <password> <owner-key> "<server name>"
# Expects the published Linux binary and the unit file in /tmp/cs2mp/ (see deploy/vps/README.md).
# Re-running only replaces the binary and unit; /etc/cs2-multiplayer/env is kept once it exists.
set -euo pipefail

PASSWORD="${1:-}"
OWNER_KEY="${2:-}"
SERVER_NAME="${3:-CS2 server}"
APP=/opt/cs2-multiplayer
ENV_FILE=/etc/cs2-multiplayer/env
STAGE=/tmp/cs2mp

# scp from Windows drops the execute bit, so test for the file and let install(1) set the mode.
if [ ! -f "$STAGE/Multiplayer.Server" ]; then
  echo "missing $STAGE/Multiplayer.Server" >&2
  exit 1
fi

id -u cs2mp >/dev/null 2>&1 || useradd --system --home-dir "$APP" --shell /usr/sbin/nologin cs2mp
mkdir -p "$APP" "$APP/.net" /etc/cs2-multiplayer

systemctl stop cs2-multiplayer 2>/dev/null || true
install -m 0755 -o cs2mp -g cs2mp "$STAGE/Multiplayer.Server" "$APP/Multiplayer.Server"

# Everything the server needs is in server.json; the env file only points the .NET bundle at a writable folder.
json_escape() { printf '%s' "$1" | sed -e 's/\\/\\\\/g' -e 's/"/\\"/g'; }
read_env() { grep -E "^$1=" "$ENV_FILE" 2>/dev/null | head -1 | cut -d= -f2- | sed -e 's/^"//' -e 's/"$//'; }

CONFIG="$APP/server.json"
if [ ! -f "$CONFIG" ]; then
  # First install, or an older install that kept its settings in the env file: build server.json from what we have.
  [ -z "$PASSWORD" ] && PASSWORD="$(read_env CS2MP_PASSWORD)"
  [ -z "$OWNER_KEY" ] && OWNER_KEY="$(read_env CS2MP_OWNER_KEY)"
  OLD_NAME="$(read_env SERVER_NAME)"; [ -n "$OLD_NAME" ] && [ "$SERVER_NAME" = "CS2 server" ] && SERVER_NAME="$OLD_NAME"
  OLD_PORT="$(read_env PORT)"; [ -z "$OLD_PORT" ] && OLD_PORT=27015
  OLD_GAME="$(read_env GAME_VERSION)"; [ -z "$OLD_GAME" ] && OLD_GAME="1.6.2f1"
  OLD_MODCHECK="$(read_env CS2MP_MOD_CHECK)"; [ -z "$OLD_MODCHECK" ] && OLD_MODCHECK="names"
  OLD_PLAYSET_ID="$(read_env CS2MP_PLAYSET_ID)"; [ -z "$OLD_PLAYSET_ID" ] && OLD_PLAYSET_ID=0
  OLD_HINT="$(read_env CS2MP_PLAYSET)"
  if [ -z "$OWNER_KEY" ]; then
    echo "first install needs an owner key: setup-vps.sh <password> <owner-key> \"<server name>\"" >&2
    exit 1
  fi
  cat > "$CONFIG" <<EOF
{
  "_help": "Settings of the multiplayer server. Edit and save; the server picks changes up within ten seconds. port, ownerKey, gameVersion and dataDir need a restart (sudo systemctl restart cs2-multiplayer).",
  "name": "$(json_escape "$SERVER_NAME")",
  "port": $OLD_PORT,
  "password": "$(json_escape "$PASSWORD")",
  "ownerKey": "$(json_escape "$OWNER_KEY")",
  "_ownerKey": "Whoever enters this under Join > Owner key is the host. Keep it out of the group chat.",
  "gameVersion": "$(json_escape "$OLD_GAME")",
  "_gameVersion": "Players must run exactly this game build (Logs/Multiplayer.log on a PC prints it). Empty = not checked.",
  "maxPlayers": 8,
  "modCheck": "$(json_escape "$OLD_MODCHECK")",
  "_modCheck": "names = same mods as the host at any version. strict = same versions too. off = no check.",
  "playsetId": $OLD_PLAYSET_ID,
  "_playsetId": "Public Paradox playset to follow (the number in its URL). Its published mod list becomes the required list, re-read every few minutes. 0 = off.",
  "playsetHint": "$(json_escape "$OLD_HINT")",
  "_playsetHint": "Extra text shown to rejected players, for example where to find the playset.",
  "requiredMods": [],
  "_requiredMods": "Fixed list instead of a playset: Paradox mod ids as strings (\"125342\") or names for local mods. Empty = learn the list from the host when they join.",
  "welcome": "",
  "_welcome": "Chat message sent to every player who joins. Empty = none.",
  "updateCheck": true
}
EOF
  chown root:cs2mp "$CONFIG"
  chmod 0640 "$CONFIG"
  echo "wrote $CONFIG"
fi

# The env file must not carry settings any more, or they would override server.json.
{
  echo "DOTNET_BUNDLE_EXTRACT_BASE_DIR=$APP/.net"
} > "$ENV_FILE"
chown root:cs2mp "$ENV_FILE"
chmod 0640 "$ENV_FILE"

chown -R cs2mp:cs2mp "$APP"
install -m 0644 "$STAGE/cs2-multiplayer.service" /etc/systemd/system/cs2-multiplayer.service
systemctl daemon-reload

if command -v ufw >/dev/null 2>&1; then
  ufw allow 27015/tcp comment "CS2 multiplayer" >/dev/null
fi

systemctl enable --now cs2-multiplayer
sleep 3
systemctl --no-pager --lines=6 status cs2-multiplayer || true
echo "--- listening ---"
ss -ltnp | grep 27015 || echo "port 27015 not listening"
