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
- one-click portable SteamCMD installation from Valve on Windows and Linux, also available as `pzasm steamcmd install`;
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
- guided `Zomboid/Server/*.ini` editor for identity, access, RCON, gameplay, backups, and content, plus the complete raw editor with encoding preservation;
- safe pack application that only replaces `WorkshopItems`, `Mods`, and `Map`;
- RCON status, `save`, `quit`, Windows/Linux startup, and an optional coordinated restart when a local profile is explicitly selected;
- Windows/Linux CLI for desktop-free and SSH-managed hosts;
- `automation run` CLI daemon with inter-process locks when the UI and CLI are active at the same time.

### Project command and update workflow

Build, Update mods, and Publish are presented as the project's primary commands. Destructive or consequential actions always use an in-app confirmation window instead of browser-native dialogs. Authors and rights holders are prefilled from each source `mod.info` when available and remain editable. Every mod can be excluded from the global Update mods command and refreshed individually; excluded snapshots stay pinned until their individual update is explicitly requested.

## Getting started

Building from source requires Windows or Linux and the [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0). Self-contained CI artifacts do not require the .NET runtime. SteamCMD can be installed from the dashboard, a project’s Distribution tab, or the CLI. Public source items are downloaded anonymously by default; publishing still requires the owner account.

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

## Recommended workflow

1. Create a project and keep **Bundle** mode.
2. Describe the pack and keep the connection notice enabled.
3. Add detected mods or import a Workshop ID. Known dependencies are added and their content is pinned immediately.
4. Record the author and permission or license evidence for every source.
5. Review mod and map order.
6. Build locally and inspect `pack.lock.json` and `server-config.txt`.
7. Install SteamCMD in one click, configure the publisher account, authenticate manually once, and publish with private visibility.
8. Test on a staging server.
9. Apply the pack from the Servers page; PZASM backs up the `.ini` first.

## Headless CLI

The CLI uses the same projects as the UI. Every `project create` command creates an independent global pack with its own future Workshop ID. Later `project publish` operations update that same item.

```bash
# Local inventory
dotnet run --project src/PZAdvancedServerManager.Cli -- scan
dotnet run --project src/PZAdvancedServerManager.Cli -- steamcmd install
dotnet run --project src/PZAdvancedServerManager.Cli -- workshop search --query "map Build 42" --sort subscribed

# Create and populate a pack
dotnet run --project src/PZAdvancedServerManager.Cli -- project create --name "Primary server"
dotnet run --project src/PZAdvancedServerManager.Cli -- project add --id <guid> --mod-id damnlib
dotnet run --project src/PZAdvancedServerManager.Cli -- project import-workshop --id <guid> --workshop-id 1234567890
dotnet run --project src/PZAdvancedServerManager.Cli -- project validate --id <guid>
dotnet run --project src/PZAdvancedServerManager.Cli -- project build --id <guid>

# Schedule the same pack from an SSH session
dotnet run --project src/PZAdvancedServerManager.Cli -- project configure --id <guid> --server servertest --automation true --schedule "04:00,16:00"

# Explicit publication
dotnet run --project src/PZAdvancedServerManager.Cli -- project publish --id <guid> --yes

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

GitHub Actions tests Windows and Linux and produces self-contained `win-x64` and `linux-x64` artifacts containing the local UI and headless CLI. Do not expose the PZASM port to the Internet: the UI is a local administration tool and does not provide network authentication.

The project targets .NET 9 and requires no database. JSON writes are atomic, and snapshots and builds are prepared in temporary directories before replacement.

## Known limitations

- Public Project Zomboid Workshop sources support anonymous SteamCMD downloads; restricted or private items can still require an authenticated account.
- SteamCMD downloads known Workshop IDs but does not expose a complete search command. The internal catalog enumerates public Steam Community browse results and resolves item metadata through Steam's public details API before SteamCMD downloads the selection.
- Workshop publication relies on the Steam account session; PZASM never stores passwords or Steam Guard codes.
- Linux SteamCMD may require the distribution's 32-bit runtime libraries; the installer reports bootstrap errors without hiding the extracted tool.
- A new item can remain hidden until the Workshop legal agreement is accepted.
- A source update can change dependencies, Mod IDs, maps, or licensing and may be blocked during validation.
- Strict Fusion does not rewrite Lua namespaces, script IDs, textures, models, vehicles, or maps.
- Mods containing binaries or extensions rejected by the Project Zomboid Workshop validator are blocked.
