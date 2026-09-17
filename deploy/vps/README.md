# Running the server window on a Linux box

The window is a normal program; the game is not needed on the server. This folder installs it as a
hardened systemd service on Ubuntu (tested on the OVH VPS, Ubuntu 26.04, x86_64).

## Get the Linux binary

Easiest: download `cs2-multiplayer-server-linux-x64-<version>.zip` from the GitHub Releases page; it
contains `Multiplayer.Server`, this README, `setup-vps.sh` and the systemd unit. Or build it yourself:

## Build the Linux binary (on the dev PC)

```bash
dotnet publish Multiplayer.Server/Multiplayer.Server.csproj -f net8.0 -r linux-x64 --self-contained -p:PublishSingleFile=true -p:DebugType=none -o out/linux
```

Self-contained, so the box needs no .NET runtime. One file of roughly 70 MB.

## Install or update (from the dev PC)

```bash
ssh <box> 'mkdir -p /tmp/cs2mp'
scp out/linux/Multiplayer.Server deploy/vps/cs2-multiplayer.service deploy/vps/setup-vps.sh <box>:/tmp/cs2mp/
ssh <box> 'sudo bash /tmp/cs2mp/setup-vps.sh "<password>" "<owner-key>" "<server name>"'
```

First run creates `/etc/cs2-multiplayer/env` (port, game version, name, and the secrets as
`CS2MP_PASSWORD` / `CS2MP_OWNER_KEY`, which the program reads from the environment so they never show
in `ps`), the `cs2mp` user, `/opt/cs2-multiplayer`, opens 27015/tcp in ufw and starts the service.
Later runs only replace the binary and unit file.

## Day to day

```bash
sudo systemctl status cs2-multiplayer
```

```bash
sudo journalctl -u cs2-multiplayer -f
```

The same lines also land in `/opt/cs2-multiplayer/Multiplayer.Server.log`.

All settings are in `/opt/cs2-multiplayer/server.json`. Edit and save; the server re-reads it within ten
seconds and says in the log what changed. Keys: `name`, `port`, `password`, `ownerKey`, `gameVersion`,
`maxPlayers`, `modCheck` (`names` = same mods as the host at any version; `strict` = same versions too;
`off` = no check), `playsetId` (a **public Paradox playset to follow**: its published mod list becomes the
required list, re-read every 5 minutes, and connected players are told what was added or removed when a
new version appears), `playsetHint` (extra text for rejected players), `requiredMods` (a fixed list of mod
ids as strings when no playset is followed; empty = take the list from the host), `welcome` (chat line
sent to everyone who joins) and `updateCheck`. Port, owner key, game version and the data folder only
change on `sudo systemctl restart cs2-multiplayer`. Environment variables (`CS2MP_PASSWORD` and friends)
and command-line options still override the file if you use them. After a game patch, `gameVersion`
must match what `Logs\Multiplayer.log` prints on the players' PCs, or they are rejected with a clear
reason.

## Joining

Options > Multiplayer > Join: address `<box ip>:27015`, the password, and (host only) the owner key.
Anyone with the owner key becomes the session owner; keep it out of the group chat.

The shared city lives in `/opt/cs2-multiplayer/world/` (`world.bin`, `world.meta`, `mods.txt`) and survives
restarts. Delete those three files and restart the service to start fresh. To seed a city without the game,
the console client can upload any `.cok` save as the owner:

```bash
dotnet run --project Multiplayer.SmokeClient -- -address <box ip>:27015 -password <pw> -owner-key <key> -game-version 1.6.2f1 -upload "path/to/City.cok" -city "City"
```

Admin commands (`say`, `kick`, `speed`, `world`, `mods`, `stop`) need an interactive console and are not
reachable through systemd yet; use `systemctl restart` to clear the session. A small admin socket is a
later milestone.
