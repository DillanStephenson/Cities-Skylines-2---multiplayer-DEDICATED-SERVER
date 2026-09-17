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

if [ ! -f "$ENV_FILE" ]; then
  if [ -z "$OWNER_KEY" ]; then
    echo "first install needs an owner key: setup-vps.sh <password> <owner-key> \"<server name>\"" >&2
    exit 1
  fi
  {
    echo "PORT=27015"
    echo "CS2MP_PASSWORD=$PASSWORD"
    echo "CS2MP_OWNER_KEY=$OWNER_KEY"
    echo "GAME_VERSION=1.6.2f1"
    echo "SERVER_NAME=\"$SERVER_NAME\""
    echo "MAX_PLAYERS=8"
    echo "CS2MP_MOD_CHECK=names"
    echo "CS2MP_PLAYSET="
    echo "CS2MP_PLAYSET_ID="
    echo "CS2MP_UPDATE_CHECK=on"
    echo "DOTNET_BUNDLE_EXTRACT_BASE_DIR=$APP/.net"
  } > "$ENV_FILE"
  chown root:cs2mp "$ENV_FILE"
  chmod 0640 "$ENV_FILE"
  echo "wrote $ENV_FILE"
else
  # Older env files named the secrets PASSWORD / OWNER_KEY; rename them in place.
  sed -i -e 's/^PASSWORD=/CS2MP_PASSWORD=/' -e 's/^OWNER_KEY=/CS2MP_OWNER_KEY=/' "$ENV_FILE"
fi

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
