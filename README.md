# Multiplayer for Cities: Skylines II

Co-op multiplayer built around a **server window**: a small console app that owns the session, shows who is
connected and what is happening, and does the networking work. The player who clicks **Host** gets that
window opened next to their game automatically and joins it as the owner; friends join the same window by
address. Nobody has to rent or install a dedicated server, but the very same window can run on a remote box
if a group wants one.

Status: **milestone 3** (build sync). Everything from milestones 1 and 2 plus: what a player builds,
places, zones, bulldozes or terraforms is captured as the game's own "definition" entities at the moment
of the click, relayed through the window, and re-applied on every other player's game by the game's own
generators; taxes, budgets, fees and policies (city-wide and per building, district or line) travel as
named settings. Verified in-game on one PC for roads, trees, zoning, bulldozing, terrain, taxes and a
building switch-off, each with the round trip through the server and back. The simulation itself
(citizens, traffic, money over time) still runs separately on each PC; the host's periodic upload is the
correction for that.

## Layout

| Project | Target | Purpose |
| --- | --- | --- |
| `Multiplayer/` | net48 (game) | The mod. `Mod.cs` entry point, `Setting.cs` options page, `MultiplayerService` (spawns/joins the server window), `NetworkingSystem` (per-frame pump + speed sync), `DevTriggers` (headless host/join for smoke tests). |
| `Multiplayer.Server/` | net48 + net8.0 | **The server window.** `ServerSession` host, status panel, command line (`list`, `say`, `speed`, `kick`, `stop`). The net48 build ships inside the mod folder so it runs on any Windows PC with nothing to install. |
| `Multiplayer.Core/` | netstandard2.0 | Game-independent networking: wire format (`Protocol/`), framed TCP transport (`Transport/Tcp/`), `ServerSession` and `ClientSession` (`Session/`). Compiled **into** the mod DLL and the server exe via a `Compile Include` glob, so each is a single file. |
| `Multiplayer.Tests/` | net8.0 xunit | Loopback and wire-format tests (64): codec, framing, handshake, rejection reasons, owner key, chat, speed, drops, timeouts, kick, owner stop, world transfer, mod lists, build, city state and policy commands. |
| `Multiplayer.SmokeClient/` | net8.0 console | Joins a real server window using the same core, so one PC can verify host/join end to end. |
| `Multiplayer.UI/` | React + TypeScript | The main-menu screen (MULTIPLAYER entry, Join and Host forms, session status), built with the game's own UI mod template into `Multiplayer.mjs` + `.css` next to the DLL. `MultiplayerUISystem` in the mod holds the bindings it talks to. |

## Build and test

```bash
dotnet test Multiplayer.Tests/Multiplayer.Tests.csproj
```

```bash
dotnet build Multiplayer/Multiplayer.csproj
```

Building the mod runs the game's ModPostProcessor (Burst/IL post-processing), builds the net48 server
window, copies it into the output, and deploys everything to
`%USERPROFILE%\AppData\LocalLow\Colossal Order\Cities Skylines II\Mods\Multiplayer`, where the game picks
it up on next launch. The csproj overrides `ModPostProcessorConfig` to run the post-processor with
`DOTNET_ROLL_FORWARD=LatestMajor` because that tool targets .NET 6 and only 8/9/10 are installed here.

## How it works

```
                        server window (Multiplayer.Server.exe)
                        ┌──────────────────────────────────────┐
                        │ ServerSession: roster, relay, speed  │
                        │ status panel + admin commands        │
                        └───────▲──────────────────▲───────────┘
                     TCP frames │                  │ TCP frames
        ┌───────────────────────┴──┐            ┌──┴───────────────────────┐
        │ host's game (owner key)  │            │ friend's game             │
        │ NetworkingSystem         │            │ NetworkingSystem          │
        │   └ MultiplayerService   │            │   └ MultiplayerService    │
        │       └ ClientSession    │            │       └ ClientSession     │
        └──────────────────────────┘            └───────────────────────────┘
```

* **Transport**: length-prefixed frames over TCP, one receive and one send thread per socket, events
  drained on the owning thread. `ITransport` is the seam for a Steam relay transport later.
* **Server session**: the authority. Handshake checks protocol version, mod version, game version,
  password and (optionally) the owner key. Heartbeats every 2 s, peers dropped after 10 s of silence,
  silent sockets kicked after 8 s. Relays chat to everyone, applies and broadcasts speed requests,
  forwards gameplay commands to every other player. Only the owner may ask it to stop.
* **Client session**: what every game instance runs. Host = the mod starts the window with a fresh random
  owner key, `--parent-pid` (the window exits when the game does) and `--exit-when-owner-leaves`, then
  joins `127.0.0.1` with that key, retrying for up to 15 s while the window starts.
* **Speed sync**: `NetworkingSystem` watches `SimulationSystem.selectedSpeed`; a local change is sent as a
  request and whatever the server announces is written back.
* **Gameplay commands**: `GameplayCommandMessage { Kind, OriginPlayerId, Payload }` is relayed by the
  server to every other player. Build sync uses kind `build`.
* **Build sync** (`Multiplayer/Sync/`, wire format in `Multiplayer.Core/Build/`). How the game's tools work:
  each frame the active tool emits small *definition* entities (`CreationDefinition` plus `NetCourse`,
  `ObjectDefinition`, `Zoning`, area nodes ...), generator systems turn those into temp entities, and when
  the player clicks the output system runs the apply phase, which realises the temps. Two of our systems
  sit in that pipeline:
  * `BuildCaptureSystem` runs in the apply phase just before the game's `ToolApplySystem`. The definitions
    that produced the temps being applied are still alive there, so it serialises them (prefabs by type and
    name, referenced entities by prefab and position via `EntityResolver`) and sends one `BuildCommand`.
  * `BuildReplaySystem` runs in the tool phase just before the game's `ToolOutputSystem`. For a queued
    command it waits for the default tool (borrowing the player's tool after 1.5 s if needed), clears stray
    temps, recreates the definitions with `Updated`, lets the generators build the temps that frame, and next
    frame sets the default tool's apply mode to Apply so the game realises them. `SyncGuard` keeps the
    capture system quiet for that frame. Money is charged on every game because the apply system does it.
  Covered generically: roads and every other net, buildings, props, trees, brushes, zoning, districts and
  lots, bulldozing, moving and upgrading, terrain sculpting and resource painting (the terrain tool emits
  one brush definition per frame while the mouse is held; the receiver applies consecutive strokes together
  so it never falls behind a drag), and transit line editing (waypoint buffer plus line colour; lines are
  referenced by prefab and line number, waypoints and stops by position). Anything that goes through
  definitions, in short. Definitions the simulation itself emits (flag `Permanent`, such as driveways for
  spawned buildings) are filtered out, since every game produces those on its own. Divergence: if a
  validation error fires on the receiving game (something already in the way), that part is skipped and the
  log names the error and the prefab; the host's next upload realigns everyone. Transit line editing is
  wired through the same path but has not been exercised in-game yet.
* **City settings** (`CityStateSyncSystem`, wire format `CityStateCommand`): tax rates (per area, per
  residential level, per resource for commercial, industrial and office), service budgets, service fees
  and city policies are read twice a second as named numbers. What changed locally is broadcast; what
  arrives is applied through the game's own setters (`TaxSystem`, `CityServiceBudgetSystem`,
  `ServiceFeeSystem`, `PoliciesUISystem`) and recorded as synced so it is not sent back. The host also sends
  a full snapshot once a minute so late joiners converge. Last write wins when two players change the same
  thing at once.
* **Building, district and line options** (`PolicySyncSystem`, wire format `PolicyCommand`): the game turns
  every panel toggle on a building, district or transport line (switch off, paid parking, district rules,
  ticket price ...) into an event entity that its `ModifiedSystem` consumes. This system runs just before
  that, sends new local events as "policy X on/off (adjustment) for <prefab at position>", and creates
  identical event entities for changes that arrive, so the game applies them exactly as it would a click.
  Events it has handled carry a tag so they are never sent twice.
* **Shared city** (`WorldSyncService` in the mod, `WorldStore` in the window): the host's game calls the
  game's own save path (`GameManager.Save` into `AssetDatabase.user`, named `MP <server>`), reads the
  `.cok` package back and streams it in 256 KB chunks with a SHA-256. The window keeps `world.bin` +
  `world.meta` in its data directory and announces `WorldInfo` (revision, size, hash, city) to every
  joiner. A joiner writes the package plus its `.cid` into their own save folder, registers it with the
  file-system data source, and loads it exactly as the Load Game menu would. Rules: the server copy is the
  shared city; a player at the main menu, or a guest who has not loaded it yet, fetches it automatically;
  someone already in a city is told and can press **Get the shared city**; the host uploads when the server
  has nothing, on **Upload my city**, and every N minutes while in sync (default 5, 0 = off).
* **Mods**: every client sends its content list in the handshake, built from the active Paradox playset
  (asset packs included, since a shared city can depend on them) plus local code mods, as
  `Name [pdx ID vN]` or `Name [local]`, together with the name of the active playset. The owner's list
  becomes the reference, stored as `mods.txt` next to the world (with the playset name as a header line);
  a guest whose list differs is rejected with a message naming what is missing or extra, plus "the host
  plays with the playset 'Shared Mods'; activate it in Paradox Mods". By default the same Paradox mod at
  another version still matches, because a shared playset updates on each PC at its own pace; the window
  takes `--mod-check strict|names|off` (or `CS2MP_MOD_CHECK`) to tighten or drop the check, and
  `--playset TEXT` (or `CS2MP_PLAYSET`) to add the public playset id to the message. Unlike the other
  multiplayer mod, nothing is blocked: any mod is allowed as long as everyone runs the same set, and mod
  data stored in the save (Traffic Tool Essentials, for example) travels with the shared city. The
  practical recipe for a group: the host publishes their playset on Paradox Mods, everyone activates it.
  The window can also **follow that published playset** (`--playset-id N` or `CS2MP_PLAYSET_ID`): it reads
  the playset's mod list from the public Paradox Mods API on start and every few minutes, uses it as the
  reference, announces additions and removals to connected players when a new public version appears, and
  warns the host when their own game differs from what is published. Adding a mod then means: add it to
  the playset, press Update public version, and everyone restarts with the updated playset.

## Using it

Main menu > **MULTIPLAYER** (after Load Game). **Join game**: player name, address (`host:port`), password
and, for the host only, the owner key. **Host game**: port and optional password; the server window opens
by itself and the game joins it. The status block below the form shows the connection, the players, the
shared city and its transfer, and the last few events. Joining from the main menu fetches the shared city
and loads it. The same fields also live under Options > Multiplayer. Remote friends need the host's TCP
port forwarded; Steam relay (no port forwarding) is on the roadmap.

In a city, the round Multiplayer button at the top of the right-hand stack (green dot = connected) opens a
small panel: status, players, what the server holds, the last few events, and **Save to server** (owner),
**Get the newer city** (when the server has a newer revision than the one loaded) and **Disconnect**.

The screen is the game's credits sub-screen with our content swapped in while the C# side flags it active
(the menu offers mods no other way to open a full sub-screen with its backdrop and back button); the menu
entry is appended to the main-menu navigation list. Building the C# project runs `npm run build` in
`Multiplayer.UI` when its `node_modules` exists (`npm install` there once) and copies the bundle into the
output before the toolchain deploys it.

Running the window by hand (dedicated setup): `Multiplayer.Server.exe --port 27015 --password x
--game-version 1.6.2f1`. It prints an owner key; whoever enters it under Join > Owner key becomes the host.
`--help` lists the rest; `CS2MP_PASSWORD` and `CS2MP_OWNER_KEY` in the environment keep the secrets out
of the process list. The window shows an ASCII skyline banner, a pinned status block (port, owner key,
players, speed, traffic) and a colour-coded event log above the command line.

**On a Linux server**: see [deploy/vps/README.md](deploy/vps/README.md). A self-contained build plus
`setup-vps.sh` installs it as a hardened systemd service; the game is not needed on the box.

### Headless smoke test on one PC

1. Create `%USERPROFILE%\AppData\LocalLow\Colossal Order\Cities Skylines II\ModsSettings\Multiplayer\dev-autohost.txt`
   (empty, or containing a port). The mod hosts as soon as it loads: the window opens and the game joins it.
2. Launch the game, then run the console client with the game version printed in `Logs\Multiplayer.log`:

```bash
dotnet run --project Multiplayer.SmokeClient -- -address 127.0.0.1:27015 -name Smoke -game-version 1.6.2f1 -seconds 20
```

3. Delete the trigger file afterwards. `dev-autojoin.txt` (address, then optional password and owner
   key on the next lines) does the opposite.

Build sync on one PC: add `dev-build.txt` next to the other triggers, launch with `--continuelastsave`,
and run the console client with `-echo-build 150 -echo-state 1 -echo-policy 1`. Once in the city the mod
runs a sequence through its own replay engine: a short road on flat ground, a tree beside it, a residential
zoning fill on the road's blocks, a bulldoze of the road, a residential tax change, two terrain raise
strokes, and a "switch off" on the first city service building. The capture system broadcasts each step;
the console client echoes builds back shifted 150 m, the tax change one point higher and the policy
inverted; the game replays those. `Logs\Multiplayer.log` gets a "Dev check" line after every step
comparing the original spot with the echo spot (for the policy: it should read inactive again). The
sequence leaves the city as it found it apart from the terrain bump.

Launching note: starting `Cities2.exe` by hand fails the platform-services step unless the Steam
environment is present. Launch through Steam (and click Play in the Paradox launcher), or run the exe with
`SteamAppId=949230` and `SteamGameId=949230` set in the environment.

## Publishing

One repository, two things people install:

* **The mod**, on Paradox Mods. The package is the deployed mod folder: `Multiplayer.dll`, the UI bundle
  (`Multiplayer.mjs` + `.css`) and the bundled Windows server window, which is what **Host game** starts.
  Text, thumbnail and version live in `Multiplayer/Properties/PublishConfiguration.xml`. From this folder,
  with the game closed and the Paradox launcher signed in:

  ```bash
  dotnet publish Multiplayer/Multiplayer.csproj -c Release -p:PublishProfile=PublishNewMod
  ```

  The first publish prints the new mod id; put it in `<ModId>` and use the `PublishNewVersion` profile for
  every later build (bump `<ModVersion>` and the change log first) or `UpdatePublishedConfiguration` for
  text and image changes only. `<AccessLevel>` starts as Private so the page can be checked before it is
  public. The csproj runs the publisher with `DOTNET_ROLL_FORWARD=LatestMajor` for the same reason as the
  post-processor.
* **The dedicated server**, on GitHub Releases. Pushing a tag such as `v0.2.0` makes the workflow in
  `.github/workflows/build.yml` run the tests and attach self-contained server builds for Linux and
  Windows (`cs2-multiplayer-server-<rid>-<tag>.zip`, with the VPS setup script inside). Nobody needs the
  game or .NET on the box.

## Roadmap

1. ~~Session foundation~~.
2. ~~Shared city~~: done without Harmony by going through the game's real save files instead of an
   in-memory stream. Verified on one PC (game uploads 39.6 MB, relaunch fetches and loads it) and against
   the VPS (upload, restart-safe storage, byte-identical download).
3. ~~Build sync~~: definition capture and replay, verified on one PC for roads, trees, zoning,
   bulldozing and terrain, plus city settings (taxes, budgets, fees, policies) and per-building options.
   Transit line editing is wired but unverified. Next within it: a play test with two PCs and a per-mod
   settings snapshot in the handshake.
4. **Steam relay transport**: `Steamworks.SteamNetworkingSockets` P2P (the game ships Steamworks.NET),
   so nobody has to forward ports.
5. ~~Main-menu UI~~: MULTIPLAYER entry with Join and Host screens and live status, plus the in-game
   toolbar panel (save to server, fetch, disconnect). Still to do: chat and player cursors.

## Prior art

* Rollocraft's *CS2 Multiplayer Mod* (Paradox Mods 150432, source on GitHub, non-commercial licence):
  host-authoritative, in-process host, Steam relay or direct TCP, extensive sync. No separate server.
* CitiesSkylinesMultiplayer's *CS2M* (MIT): early skeleton, LiteNetLib, in-process host, external API
  server for NAT hole punching. Its `SaveLoadHelper` shows the save/load-from-stream trick.
* *MultiSkyLinesII*: separate cities trading resources, not a shared city.
