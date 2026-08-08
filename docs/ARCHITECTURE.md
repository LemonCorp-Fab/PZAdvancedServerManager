# Architecture and feasibility study

[English](ARCHITECTURE.md) · [Français](ARCHITECTURE.fr.md) · [Español](ARCHITECTURE.es.md) · [Deutsch](ARCHITECTURE.de.md) · [Português (Brasil)](ARCHITECTURE.pt-BR.md) · [简体中文](ARCHITECTURE.zh-CN.md)

## Conclusion

Blindly concatenating every `media` directory is not the right model. Project Zomboid already supports loading several logical mods from one Workshop item. PZASM uses that distinction:

```text
one Workshop PublishedFileId
└── mods/
    ├── ModA/          → mod.info: id=ModA
    ├── ModB/          → mod.info: id=ModB
    └── PZASM_Notice/  → mod.info: id=PZASM_Notice_SUFFIX
```

The game still sees several **Mod IDs**, because its loader needs them, but it sees only **one Workshop ID to synchronize**. This achieves the version-stability objective without the risks of a physical file merge.

## What the Project Zomboid client checks

Inspection of the local 42.20.2 installation shows two distinct multiplayer connection phases:

1. the Workshop list received from the server contains each item ID and timestamp; the client compares the installed/published timestamp and reports a version mismatch for that item;
2. the `Mods=` list is then loaded by Mod ID. The Mod ID-to-Workshop mapping can explain a missing mod, but no separate Workshop timestamp is attached to every logical mod contained in one item.

The normal multiplayer integrity phase follows, including Lua checksums when `DoLuaChecksum=true`. Bundle mode therefore removes drift between many independently updated Workshop items without disabling the game's consistency checks.

This finding applies to the inspected version and should be covered by compatibility tests after major Project Zomboid updates.

## On-disk layout

Workshop app `108600` items commonly use this structure:

```text
steamapps/workshop/content/108600/<WorkshopId>/
├── mods/
│   └── <LogicalFolder>/
│       ├── mod.info                  # legacy manifest and fallback
│       ├── media/                    # legacy content
│       ├── common/
│       │   ├── mod.info
│       │   └── media/
│       ├── 42.0/
│       │   ├── mod.info
│       │   └── media/
│       └── 42.13/
│           ├── mod.info
│           └── media/
└── workshop.txt or other local source metadata
```

One Workshop item can already contain several logical mod folders. `mod.info` provides fields such as `name`, `id`, `author`, `description`, `poster`, `require`, version constraints, and free-form metadata. A `media` directory may contain:

- `lua/client`, `lua/server`, and `lua/shared`;
- scripts and item, vehicle, recipe, or distribution definitions;
- maps, lots, cells, spawn regions, and zones;
- textures, UI, models, animations, sounds, radios, translations, and other assets.

Bundle mode preserves each complete source directory. Analysis and Strict Fusion compose effective content in this order: legacy `media`, `common/media`, then the highest compatible numeric version.

## Why a complete merge is risky

Independent mods can use the same relative destination for unrelated objects:

- Lua modules, `require` paths, duplicate events, or global names;
- script item, recipe, vehicle, or distribution IDs;
- texture and model resource names;
- map cells, folders, lots, and zones;
- translation or UI keys;
- Java classes, JAR files, native loading, and runtime-specific code.

Renaming files is insufficient because references can exist in Lua, scripts, models, maps, or bytecode. Strict Fusion therefore follows a deterministic rule: identical file means deduplication; a newer compatible layer of the same mod overrides its older layer; different content from different mods at the same destination is a build error.

## Packaging modes

### Bundle — recommended

- one Workshop ID;
- the original Mod IDs plus the optional notice;
- each source directory copied without semantic rewriting;
- available `require=` dependencies included;
- exhaustive description and lockfile;
- maximum compatibility with `getActivatedMods()`, `getModInfoByID()`, and `getModFileReader()`.

### Strict Fusion — advanced

- one Workshop ID and one generated `PZASM_Pack_<suffix>` Mod ID;
- effective `media` content merged;
- no silent collision decisions;
- suitable only for controlled, tested mod sets;
- incompatible with some mods that inspect their original Mod ID or root directory.

## Durable projects and pinned versions

Every project has an immutable GUID, and its PZASM Mod ID suffix is derived from that GUID. The project also retains Steam's `publishedfileid`:

- `0`: SteamCMD creates an item;
- after success: SteamCMD rewrites the VDF with the new ID, which PZASM saves;
- later publications: the same ID is reused and updated.

When a source is added, PZASM copies it into a private project snapshot and records a SHA-256 tree hash. Builds use the pinned copy, not the mutable Steam Workshop cache. A source refresh is an explicit lifecycle operation that downloads, validates, and atomically replaces the snapshot.

The public lockfile records every delivered source and file hash. It identifies the exact contents of a build even after upstream Workshop items have changed.

## Publication and scheduling

Steam documents that `workshop_build_item` creates an item when `publishedfileid=0` and updates that field for later submissions to the same item. See the [Steamworks Workshop implementation guide](https://partner.steamgames.com/doc/features/workshop/implementation).

The PZASM scheduler:

1. determines whether a configured time is due;
2. reports permission records and validates dependencies, project state, and source files;
3. optionally downloads current source items with SteamCMD;
4. resolves every source to the matching Mod ID in the SteamCMD cache;
5. atomically replaces private snapshots and recalculates SHA-256 hashes;
6. builds exclusively from snapshots in a temporary directory;
7. for a local or SSH-startable profile, coordinates `save` and `quit` before the upload;
8. publishes the VDF to the same Workshop ID;
9. either starts the game process PZASM stopped or, for an RCON-only profile, sends `save` and `quit` after a successful upload so its supervisor reloads the pack;
10. records timestamps and results in the project.

Passwords and Steam Guard codes are never persisted. A supervised login sends the password through SteamCMD standard input. Accounts without Steam Guard continue directly. For protected accounts, SteamCMD sends a Steam Mobile approval request and polls it while the UI shows an active waiting state. The current code is requested only when mobile approval expires or the user explicitly chooses the fallback, then PZASM retries with SteamCMD's documented `set_steam_guard_code` command through standard input. Steam supports QR sign-in in its client and web pages, but SteamCMD exposes no documented QR payload or QR login command, so a separate web QR cannot establish this publishing session. SteamCMD then keeps its own portable refresh token. Manual publishing and the scheduler use only that cached session; an expired session fails with a reconnect-required result instead of waiting on a hidden prompt. PZASM records only the last successful verification time. Production automation should use a limited account and a staging server.

## Injected connection notice

The notice is a separate client Lua mod in Bundle mode and an integrated client file in Strict Fusion. On `Events.OnConnected`, it opens a scrollable window containing:

- the PZ Advanced Server Manager name;
- the chosen title and description;
- a clear rights warning;
- every source mod, author, Mod ID, and original Workshop ID.

Injection is enabled by default and can be disabled per project. The notice downloads nothing and contacts no external service.

SteamCMD is a separate Steam session, so production automation should use a dedicated publishing account that owns Project Zomboid instead of the account active in the desktop client. The first login creates the portable token; later checks use `steamcmd verify`, which supplies no password and does not create another token. PZASM never imports desktop Steam cookies or login files. A desktop-session publisher would require an authorized Steamworks application: the Project Zomboid publisher must add the tool AppID to the Workshop App Publish Permissions for `ISteamUGC`, while OAuth requires a Valve-issued client ID with AppID-scoped `write_cloud` access. An external tool cannot grant itself either permission.

## Why an external application is required

A Project Zomboid mod runs inside the game's process and lifecycle. It is not a reliable environment for:

- discovering Steam libraries before launch;
- copying and hashing large file trees;
- retaining projects and private permission evidence;
- starting SteamCMD and publishing Workshop items;
- scheduling updates while no game process is running;
- editing, backing up, starting, and stopping several server profiles.

PZASM is therefore a local ASP.NET Core application plus a shared headless CLI. Both use the same core, project format, lifecycle services, and locks. Windows x64 and Linux x64 are supported. The generated notice is the only component executed by Project Zomboid.

## Multi-project model

One project represents one independent global Workshop pack:

- its own GUID and stable suffix;
- its own `publishedfileid`, created by the first publication;
- its own sources, pinned versions, rights, maps, and coordinated server;
- later updates sent only to that same Workshop item.

Creating another project therefore creates another independent pack. The UI and CLI open the same project catalog when they use the same data root.

## Windows, Linux, and headless operation

The .NET core detects standard Steam locations on Windows and Linux, `steamcmd.exe` or `steamcmd.sh`, and `StartServer64.bat` or `start-server.sh`. The local web UI requires no native desktop toolkit.

The CLI covers inventory, project creation and duplication, source add/remove/import/refresh, permission records, validation, build, explicit publication, server configuration, status, startup, graceful shutdown, pack application, and scheduled daemon operation. It is suitable for SSH-managed servers, persistent containers, and systemd services.

The UI worker and `pzasm automation run` use the same `PackageAutomationService`. A global scheduler lock prevents duplicate schedule execution, and per-project locks prevent concurrent refresh, build, and publish operations across processes.

## Core boundaries

The front ends contain no duplicated business workflow:

- `PackageProjectService` owns creation, dependency-aware addition, snapshots, ordering, duplication, and deletion;
- `PackageLifecycleService` owns refresh, build, publish, and server coordination;
- `PackageAutomationService` is shared by the UI worker and CLI daemon;
- `ServerProfileService` owns name validation, encoding, backups, pack application, and RCON orchestration;
- `WorkshopImportService` owns SteamCMD download, discovery, and import by Workshop ID;
- `PzasmConstants` contains shared product identifiers and values.

Older projects are migrated in memory to the current schema. Snapshots and builds are prepared in temporary directories before atomic replacement. Destructive file operations are constrained to validated PZASM data roots.

## Security, rights, and publication

The [official Project Zomboid modding policy](https://projectzomboid.com/blog/modding-policy/) requires author permission for public packs and unlisted server packs. A personal copy is exempt only while it is neither published nor made downloadable. The complete source list must remain visible.

PZASM therefore enforces these rules:

- the user must acknowledge the global warning;
- every source has a permission status and evidence fields;
- permission statuses and evidence are advisory records that never block build, publication, or automation;
- unknown, missing-evidence, and denied statuses remain clearly visible as warnings so the administrator can make an informed decision;
- private evidence stays outside `Contents`;
- the generated public description contains every source;
- LemonCorp does not certify user-entered declarations and is not responsible for their accuracy.

Steam may keep a new item hidden until its contributor accepts the [Workshop legal agreement](https://steamcommunity.com/workshop/workshopsubmitinfo/).

## Remaining operational risks

- A Project Zomboid update can change the multiplayer protocol or Build 42 layout.
- A source author can change a Mod ID, dependency, map layout, or license.
- Maps can require a specific manual order.
- A mod can depend on another mod without declaring `require=`.
- Two sources can declare the same logical Mod ID.
- Client and server scripts remain subject to `DoLuaChecksum`.
- SteamCMD can require interactive account intervention.
- The server must restart after publication to load the new pack; forced process termination remains intentionally unsupported.

## Local and remote server orchestration

Profiles are either local INI files or remote VPS/dedicated connections. A remote profile may be RCON-only; SSH and remote INI management are optional. Status is not a plain TCP-port probe: PZASM authenticates through RCON and reports online only when Project Zomboid accepts the configured password. The RCON console accepts supported administration commands, and graceful stop always uses `save` then `quit`.

Local profiles have an explicit execution mode. **Hosted** profiles are started from the game client's Host menu and use a `zombie.network.GameServer -coop` process plus `coop-console.txt`. **Dedicated** profiles are launched through the separate Project Zomboid Dedicated Server Steam Tool (AppID 380870) and use `server-console.txt`. Both modes intentionally reference the same native `Zomboid/Server/<name>.ini` family; the manager stores the chosen usage separately. A `-coop` helper is not considered a running host merely because its process exists: it must show valid recent startup progress or a ready marker, and a later startup failure or shutdown marker invalidates it.

An RCON-only profile can coordinate publication when systemd, Docker, a hosting panel, or another supervisor restarts Project Zomboid after `quit`: the upload completes first, then the manager sends `save` and `quit`. SSH is limited to optional remote INI transfer, connection testing, and an optional command that starts only the game process or service. It uses a private key or SSH agent in non-interactive mode. Host `reboot`, `shutdown`, and `poweroff` commands are rejected. The RCON secret is stored in local manager profile data for unattended operation, so that directory must be protected.
