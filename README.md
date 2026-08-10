# PZ Advanced Server Manager

[English](README.md) · [Français](README.fr.md) · [Español](README.es.md) · [Deutsch](README.de.md) · [Português (Brasil)](README.pt-BR.md) · [简体中文](README.zh-CN.md)

PZ Advanced Server Manager (PZASM) is a local manager for Project Zomboid and Project Zomboid Dedicated Server. Its primary purpose is to distribute a coherent set of mods through **one Workshop ID**, so the server synchronizes the pack instead of every source item independently.

> Status: functional Windows and Linux release. Bundle mode, pinned source snapshots, the internal Workshop catalog, builds, SteamCMD publishing, standalone or coordinated scheduling, the connection notice, server management, and the headless CLI are implemented. Always test a real publication with a private item before using it in production.

## Technical verdict

The concept works without merging every `media` directory. A Project Zomboid Workshop item can contain several folders under `mods/`, each with its own `mod.info` and `id=` value. The server then references:

```ini
WorkshopItems=UNIQUE_PACK_ID
Mods=ModIdA;ModIdB;ModIdC;PZASM_Notice_SUFFIX
```

The server and clients compare the version of the single Workshop item. Internal Mod IDs are used during loading. Standard Project Zomboid Lua and checksum checks remain enabled, so an inconsistent or locally altered pack can still be rejected.

The recommended mode is **Bundle**. **Strict Fusion** creates a single Mod ID but rejects every conflicting non-identical file instead of silently choosing a winner.

See the complete [architecture and feasibility study](docs/ARCHITECTURE.md).

## Features

- detection of Steam libraries, the game, the dedicated server, SteamCMD, local mods, and Workshop app `108600` items;
- Build 41/42 layout parsing, including `common`, `42`, `42.13`, and other compatible version folders;
- reopenable `.pzasm.json` projects with stable GUID/suffix, published Workshop ID, sources, versions, order, maps, automation, and permission records;
- private SHA-256 source snapshots created when a mod is added, preventing a local Steam update from silently changing a future build;
- explicit snapshot refresh, kept separate from build and publish operations;
- direct Workshop ID import through SteamCMD, including every compatible `mod.info` and available dependency;
- internal Project Zomboid Workshop catalog with search, sorting, tags, metadata, previews, pagination, direct ID lookup, and a persistent cross-page selection cart with per-item removal;
- one shared selector for pack sources and local/dedicated-server `WorkshopItems` and `Mods` lists, while preserving raw editors;
- automatic managed SteamCMD installation from Valve on Windows and Linux at the first operation that needs it, with optional pre-installation from the UI or `pzasm steamcmd install`;
- anonymous Workshop source downloads for public server content, kept separate from the authenticated publisher account;
- project duplication and local deletion without changing source mods or deleting Workshop items;
- automatic addition of available `require=` dependencies and validation errors for missing dependencies;
- Bundle builds that preserve original folders, manifests, Mod IDs, Lua, scripts, maps, and assets;
- Strict Fusion builds with identical-file deduplication and incompatible-collision reports;
- exhaustive Workshop descriptions with author, Mod ID, original link, source Workshop ID, and permission status;
- public evidence and local private attachments, with private evidence always excluded from `Contents`;
- advisory permission records and warnings that never block build, publication, or automation; administrators retain full control and responsibility;
- an optional localized connection notice enabled by default, containing the pack description, legal notice, and exhaustive mod list with declared mod versions, PZ profiles, and pinned revisions;
- generation of `workshop.txt`, `steamcmd-item.vdf`, `server-config.txt`, preview PNG, public manifest, and SHA-256 `pack.lock.json`;
- creation and update of the same Workshop item, with the SteamCMD-written `publishedfileid` saved back into the project;
- optional daily refresh, build, and publication schedules that do not require the game server to be on the same machine;
- modern responsive project workspace with clearer grouping, folded mod-rights cards, persistent French/English/Spanish/German/Portuguese/Chinese selection, and light/dark themes (light by default);
- detailed Workshop import feedback with the current item, phase, item counter, completion percentage, analysis result, and recoverable error state;
- map-priority assistant using `map.info`, `lots=` dependencies, `.lotheader` cell conflicts, drag-and-drop ordering, and a raw `Map=` fallback;
- guided `Zomboid/Server/*.ini` editor for identity, access, RCON, gameplay, backups, and content, plus the complete raw editor with encoding preservation; local startup reads the SQLite `whitelist` and requests the initial `admin` password only when that account is actually missing;
- safe pack application that only replaces `WorkshopItems`, `Mods`, and `Map`;
- local world-data management with verified ZIP backups, player-database sidecars, restore, and fresh start; restore requires an automatic recovery backup, while fresh start offers a backup choice enabled by default;
- explicit local profile modes: game-hosted profiles launched from the Project Zomboid Host menu and local dedicated profiles launched through the separate Steam Tool AppID 380870 are grouped independently, while continuing to share the native `Zomboid/Server/<name>.ini` format;
- dynamic local runtime discovery by the exact `zombie.network.GameServer` and `-servername` arguments, including servers started before the manager; `-coop` processes count as game-hosted sessions only after valid startup progress or a ready marker, failed/idle coop helpers are ignored, standalone dedicated processes remain distinct, the graphical client alone is ignored, and duplicate active instances sharing one profile are reported as a conflict. The tabbed server view includes searchable and severity-filtered live `server-console.txt` or `coop-console.txt` output, bounded/redacted stdout/stderr, explicit startup/ready/degraded/stopped phases, network details, authenticated RCON state, and a bounded command/response console;
- guarded local force-stop recovery when an exact dedicated `GameServer` process is alive but RCON is unavailable; the UI identifies its Java PID, requires an explicit destructive confirmation, never targets the graphical client or an inactive coop helper, and verifies that the process disappeared;
- Windows/Linux CLI for desktop-free and SSH-managed hosts;
- `automation run` CLI daemon with inter-process locks when the UI and CLI are active at the same time.

See [server data management](docs/SERVER-DATA-MANAGEMENT.md) for the exact backup scope, safety model, restore behavior, and CLI commands.

### Project command and update workflow

Build, Update mods, and Publish are presented as the project's primary commands. Destructive or consequential actions always use an in-app confirmation window instead of browser-native dialogs. Authors and rights holders are prefilled from each source `mod.info` when available and remain editable. Every mod can be excluded from the global Update mods command and refreshed individually; excluded snapshots stay pinned until their individual update is explicitly requested.

## Getting started

Building from source requires Windows or Linux and the [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0). Self-contained CI artifacts do not require the .NET runtime. SteamCMD is downloaded, safely extracted, and bootstrapped in the manager data directory at first use; the dashboard, Distribution tab, and CLI can prepare or reinstall it explicitly. Public source items are downloaded anonymously by default; publishing still requires the owner account.

On Windows, run `Start-PZASM.cmd`, or:

```powershell
dotnet run --project src/PZAdvancedServerManager.App -- --open-browser
```

On Linux:

```bash
chmod +x Start-PZASM.sh
./Start-PZASM.sh
```

Use `--data-root <path>` to share an explicit data directory between the UI and CLI. The UI listens only on the local machine by default at `http://localhost:5160`.

PZASM never modifies Steam sources during a build. It builds from private pinned snapshots in its own data directory.

## Docker, Coolify, and authenticated access

The production container includes the web manager, scheduler, SSH client, SteamCMD's Linux 32-bit dependencies, and automatic SteamCMD installation. All pages require an authenticated manager account. Administrators manage accounts and can revoke sessions; operators can manage packs and servers without access to user administration.

```bash
cp .env.example .env
# Set a strong PZASM_ADMIN_PASSWORD in .env.
docker compose -f compose.yaml -f compose.local.yaml up -d --build
```

Coolify can deploy the same `compose.yaml`; configure `PZASM_ADMIN_PASSWORD` as a protected variable (Compose mounts it as a read-only secret file), route the `manager` service's port `5160` through HTTPS, and retain the `pzasm-data` volume. That volume contains accounts, cookie keys, projects, SteamCMD, its portable Steam session, Workshop downloads, and builds. See [Docker and Coolify deployment](docs/DOCKER-COOLIFY.md) for health checks, backup, SteamCMD, architecture, and host-process boundaries.

## Recommended workflow

1. Create a project and keep **Bundle** mode.
2. Describe the pack and keep the connection notice enabled.
3. Add detected mods or import a Workshop ID. Known dependencies are added and their content is pinned immediately.
4. Record the author and permission or license evidence for every source.
5. Review mod and map order.
6. Build locally and inspect `pack.lock.json` and `server-config.txt`.
7. Let the manager prepare SteamCMD automatically (or prepare it immediately from Distribution), then configure a dedicated publisher account that owns Project Zomboid. Use **Create / replace session** once, then use **Verify existing session** for later checks and publish with private visibility. If Steam Guard is enabled, approve SteamCMD's mobile notification first or use a current code as a fallback. Passwords and codes are never persisted by the manager.
8. Test on a staging server.
9. Apply the pack from the Servers page; PZASM backs up the `.ini` first.

## Headless CLI

The CLI uses the same projects as the UI. Every `project create` command creates an independent global pack with its own future Workshop ID. Later `project publish` operations update that same item. Incremental publication skips only after both local fingerprints and a fresh remote Workshop read prove that nothing changed. `--force` submits every publication dimension again while Steam still reuses identical chunks. A coordinated server remains online through the upload; after changed content is confirmed, the configured grace period defaults to five minutes before `save`, `quit`, and restart.

```bash
# Local inventory
dotnet run --project src/PZAdvancedServerManager.Cli -- scan
dotnet run --project src/PZAdvancedServerManager.Cli -- steamcmd install
dotnet run --project src/PZAdvancedServerManager.Cli -- steamcmd verify --id <guid>
dotnet run --project src/PZAdvancedServerManager.Cli -- workshop search --query "map Build 42" --sort subscribed

# Create and populate a pack
dotnet run --project src/PZAdvancedServerManager.Cli -- project create --name "Primary server"
dotnet run --project src/PZAdvancedServerManager.Cli -- project add --id <guid> --mod-id damnlib
dotnet run --project src/PZAdvancedServerManager.Cli -- project import-workshop --id <guid> --workshop-id 1234567890
dotnet run --project src/PZAdvancedServerManager.Cli -- project validate --id <guid>
dotnet run --project src/PZAdvancedServerManager.Cli -- project build --id <guid>

# Schedule the same pack from an SSH session
dotnet run --project src/PZAdvancedServerManager.Cli -- project configure --id <guid> --server servertest --automation true --schedule "04:00,16:00" --restart-delay-minutes 5

# Explicit publication
dotnet run --project src/PZAdvancedServerManager.Cli -- project publish --id <guid> --yes
dotnet run --project src/PZAdvancedServerManager.Cli -- project publish --id <guid> --yes --force

# Windows/Linux server management
dotnet run --project src/PZAdvancedServerManager.Cli -- server status --name servertest
dotnet run --project src/PZAdvancedServerManager.Cli -- server stop --name servertest --yes
dotnet run --project src/PZAdvancedServerManager.Cli -- server set --name servertest --key MaxPlayers --value 32 --yes

# Headless scheduler daemon
dotnet run --project src/PZAdvancedServerManager.Cli -- automation run --interval 30
```

Run `pzasm help` for the complete command list. Publication, stop, apply, and delete operations require explicit confirmation. Nothing updates automatically until the administrator enables automation.

Reference systemd units are available in `deploy/systemd/`. Normally, run either the UI or the CLI daemon for scheduling; shared locks still prevent concurrent operations.

## Rights and responsibility

PZASM is a technical tool. It grants no rights over included mods and does not make unauthorized redistribution permissible.

The [official Project Zomboid modding policy](https://projectzomboid.com/blog/modding-policy/) distinguishes public packs, unlisted/server packs, and strictly personal copies. Public and unlisted distributions require the appropriate permissions and a complete source list. Steam also requires acceptance of its [Workshop legal agreement](https://steamcommunity.com/workshop/workshopsubmitinfo/).

The pack creator and publisher are solely responsible for permissions, credits, licenses, and third-party content. LemonCorp and PZASM contributors are not responsible for packs built or published by users.

## Development and tests

The repository includes a cross-platform `Justfile`. Install [just](https://github.com/casey/just), then run:

```text
just                 # list every recipe
just check           # formatting check, Release build, and tests
just build           # build the complete solution
just test            # run all tests
just run-ui           # start the UI and open a browser
just run-cli help     # run a CLI command
just automation      # start the headless scheduler
just publish          # publish for the current host runtime
just publish-all      # publish win-x64 and linux-x64
```

Use the `CONFIGURATION` and `PUBLISH_DIR` environment variables to override the default `Release` configuration and `publish` output directory. Additional arguments are forwarded by `build`, `test`, `run-ui`, `run-cli`, `scan`, and related recipes.

```powershell
dotnet restore
dotnet test PZAdvancedServerManager.sln
dotnet publish src/PZAdvancedServerManager.App -c Release -o publish
```

GitHub Actions tests Windows and Linux and produces self-contained `win-x64` and `linux-x64` artifacts containing the local UI and headless CLI. Network deployments must use the built-in authentication and an HTTPS reverse proxy; never expose stored server or Steam credentials over plain HTTP.

The project targets .NET 9. Pack and server data use atomic JSON writes, while manager users and roles use a dedicated SQLite Identity database. Snapshots and builds are prepared in temporary directories before replacement.

## Local and remote Project Zomboid control

Server profiles can point to a local `Zomboid/Server/*.ini` file or to a remote VPS/dedicated host. Online detection performs a real RCON authentication, so an unrelated listener on the configured port is never treated as a running Project Zomboid server.

Remote profiles can be RCON-only. RCON provides authenticated status, the command console, `save`/`quit`, and coordinated publication. With a systemd, Docker, panel, or hosting supervisor configured to restart the game after `quit`, the manager publishes first and then requests a graceful RCON restart. SSH remains optional and is used only when remote INI access or an explicit Project Zomboid start command is wanted. Host-level `reboot`, `shutdown`, and `poweroff` commands are rejected. PZASM never reboots the VPS or dedicated machine.

Remote SSH is non-interactive and uses an SSH agent or private key. The RCON password is stored in the manager's local profile data because it is required for unattended status and graceful stops; protect the PZASM data directory like the Project Zomboid server INI files.

Pine Hosting is available as a separate API backend. Enter an API key and server identifier to reuse the complete INI, SandboxVars, Lua, pack deployment, process-control, and data-management UI without SSH. Provider-native backups can be created, locked, downloaded, restored, or used as the default safety step before a fresh start. See [Pine Hosting provider](docs/PINE-HOSTING.md).

## Steam publishing identity

SteamCMD opens its own Steam session. Do not use the same account that is active in the desktop Steam client for unattended publishing: concurrent use can interrupt the desktop session or an active game. Use a dedicated publishing account that owns Project Zomboid, keep that account as the Workshop item owner, and protect the portable SteamCMD data directory.

The first interactive login creates the portable refresh token in SteamCMD's `config/config.vdf`. Later verification, manual publishing, and scheduled publishing pass only the account name and reuse that token. PZASM never copies desktop Steam cookies or login files, never stores the password or Steam Guard code, and never silently renews the token with a password. Valve documents this preserved-`config.vdf` pattern for unattended SteamCMD builds in its [uploading guide](https://partner.steamgames.com/doc/sdk/uploading).

A true **Sign in through Steam** publisher is technically possible only as an authorized Steamworks integration. Valve requires the Project Zomboid publisher to add the tool's AppID to the Workshop **App Publish Permissions** before a separate application can upload through `ISteamUGC`; Steam OAuth additionally requires a Valve-issued client ID with AppID-scoped `write_cloud` permission. PZASM cannot grant itself either permission. Until the game publisher and Valve authorize such an integration, SteamCMD is the deployable publishing provider. See Valve's [Workshop implementation guide](https://partner.steamgames.com/doc/features/workshop/implementation) and [OAuth documentation](https://partner.steamgames.com/doc/webapi_overview/oauth).

## Known limitations

- Public Project Zomboid Workshop sources support anonymous SteamCMD downloads; restricted or private items can still require an authenticated account.
- SteamCMD downloads known Workshop IDs but does not expose a complete search command. The internal catalog enumerates public Steam Community browse results and resolves item metadata through Steam's public details API before SteamCMD downloads the selection.
- Workshop publication relies on SteamCMD's portable account session. The UI and `pzasm steamcmd login` send the password through standard input and then follow SteamCMD's actual challenge: accounts without Steam Guard continue immediately, while protected accounts wait for the Steam Mobile approval notification and poll it automatically. If the approval expires or the user chooses the fallback, the current code is applied with SteamCMD's documented `set_steam_guard_code` command through standard input before retrying. Steam supports QR sign-in in its desktop client and web pages, but SteamCMD exposes no documented QR payload or login command; showing an unrelated web QR would not authorize the publishing session. SteamCMD keeps its own refresh token in its portable directory. `pzasm steamcmd verify` tests that cached session without a password or token renewal. PZASM stores only the last successful verification time, never the password or code. Scheduled and manual publishing reuse that cached session and fail with a reconnect-required result if it expires.
- Linux SteamCMD may require the distribution's 32-bit runtime libraries; the installer reports bootstrap errors without hiding the extracted tool.
- A new item can remain hidden until the Workshop legal agreement is accepted.
- A source update can change dependencies, Mod IDs, maps, or licensing and may be blocked during validation.
- Strict Fusion does not rewrite Lua namespaces, script IDs, textures, models, vehicles, or maps.
- Mods containing binaries or extensions rejected by the Project Zomboid Workshop validator are blocked.
