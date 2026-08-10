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

Publication is incremental on two levels. PZASM fingerprints the delivered content, metadata, and preview separately and omits unchanged dimensions from the generated VDF. SteamCMD/Steam then compares the submitted content manifest with its previous manifest and transfers only missing chunks. PZASM never downloads the package again after an upload.

A no-change result requires all three local fingerprints plus a fresh public API reread of the remote content handle, preview handle, file size, update time, title, description, and visibility to match the last confirmed publication. If any proof is unavailable or stale, PZASM submits conservatively. A forced publication always sends every dimension to SteamCMD, while Steam still reuses identical remote chunks. Process exit code `0` alone is not treated as success: the current SteamCMD activity must explicitly contain `Upload finished ... : OK`, and any explicit Workshop failure wins.

The PZASM scheduler:

1. determines whether a configured time is due;
2. reports permission records and validates dependencies, project state, and source files;
3. optionally downloads current source items with SteamCMD;
4. resolves every source to the matching Mod ID in the SteamCMD cache;
5. atomically replaces private snapshots and recalculates SHA-256 hashes;
6. builds exclusively from snapshots in a temporary directory;
7. leaves the coordinated server online throughout build and upload;
8. publishes the minimal VDF to the same Workshop ID and waits for explicit SteamCMD upload completion;
9. when delivered content changed and the server was online, waits the configured post-confirmation delay (five minutes minimum), then sends `save` and `quit` and applies the configured restart strategy;
10. records the locally and remotely proven state; verified no-change, metadata-only, and preview-only operations do not restart the server.

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

SteamCMD is a managed dependency, not a prerequisite. On the first import, source refresh, publication, session check, or dedicated-server repair that needs it, PZASM uses a valid configured executable or downloads Valve's platform archive into `<data-root>/tools/steamcmd`, extracts it safely, runs its bootstrap, and reuses that same portable cache and session afterward. A stale custom path falls back to the managed copy automatically. Public Project Zomboid Workshop downloads use anonymous login by default. The UI streams download, extraction, bootstrap, and verification phases, and cancellation terminates the bootstrap process as well as the request.

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
- A server restart is required only after delivered pack content changes; it happens after confirmed upload and the configured grace period, while forced process termination remains intentionally unsupported.

## Local and remote server orchestration

Profiles are either local INI files or remote VPS/dedicated connections. A remote profile may be RCON-only; SSH and remote INI management are optional. Status is not a plain TCP-port probe: PZASM authenticates through RCON and reports online only when Project Zomboid accepts the configured password. The RCON console accepts supported administration commands, and graceful stop always uses `save` then `quit`.

Local profiles have an explicit execution mode. **Hosted** profiles are started from the game client's Host menu and use a `zombie.network.GameServer -coop` process plus `coop-console.txt`. **Dedicated** profiles are launched through the separate Project Zomboid Dedicated Server Steam Tool (AppID 380870) and use `server-console.txt`. Both modes intentionally reference the same native `Zomboid/Server/<name>.ini` family; the manager stores the chosen usage separately. A `-coop` helper is not considered a running host merely because its process exists: it must show valid recent startup progress or a ready marker, and a later startup failure or shutdown marker invalidates it.

An RCON-only profile can coordinate publication when systemd, Docker, a hosting panel, or another supervisor restarts Project Zomboid after `quit`: the upload completes first, then the manager sends `save` and `quit`. SSH is limited to optional remote INI transfer, connection testing, and an optional command that starts only the game process or service. It uses a private key or SSH agent in non-interactive mode. Host `reboot`, `shutdown`, and `poweroff` commands are rejected. The RCON secret is retained for unattended operation but encrypted at rest; protect both the data directory and the deployment encryption key.

Remote operations are selected through `RemoteServerBackendRouter`. `SshRconRemoteBackend` preserves the generic VPS behavior, while `PineHostingRemoteBackend` uses the Pine/Pterodactyl client API for files, resources, power, console commands, and backups. `ServerProfileService` continues to own parsing, validation, package application, offline policy, and read-after-write checks; provider implementations only supply transport and control primitives. This keeps the configuration UI and CLI behavior identical across local, SSH, and Pine profiles.

Pine API requests are restricted to `https://panel.pinehosting.com`, use a per-request Bearer header, and validate every server, backup, and remote-path identifier. Configuration writes first create a timestamped provider-side copy and are accepted only after a successful readback. Fresh start deletes only the allowlisted Project Zomboid world and player-database targets after the optional locked safety backup has completed.

## Compatibility and conflict workbench

The pack editor and server deployment view share a cached static analyzer. It reads effective Build 42 layouts (`common` plus the best compatible version folder), `require`, `loadAfter`, `loadBefore`, `incompatible`, duplicate Mod IDs, virtual Lua/script/asset paths, map dependencies, and overlapping `.lotheader` cells. Differing files are hashed only after a shared-path and file-size check; identical content is recorded as resolved information.

The workbench proposes a stable topological mod order and a map order, exposes the exact evidence, and lets an administrator choose a priority winner, acknowledge an intentional collision, or disable a source. Manual winners become explicit order constraints and never rewrite third-party source files. Server audits also correlate the pack with `WorkshopItems`, `Mods`, `Map`, and bounded runtime log failures. Static analysis cannot prove arbitrary Lua mods compatible, so runtime testing remains mandatory.

Hard dependency order violations are blocking findings. Strongly connected components isolate the exact mods in a real cycle instead of including every downstream mod. When a cycle is caused only by a manual collision winner that contradicts `require`, `loadAfter`, or `loadBefore`, the workbench can repair it in one click: it removes only the proven-invalid manual constraint, rebuilds and validates the graph, then applies the stable topological order. The operation restores the removed constraints if validation still fails. Cycles made entirely from source-declared constraints remain explicit manual blockers.

File collisions are also classified by runtime impact: translations and passive media are low risk, client UI overrides are moderate, shared gameplay or script overrides are high, and server Lua or map data is critical. The diagnostic groups these types separately, shows the first conflicting virtual path in every header, and can open each physical source copy after validating that it remains inside a managed mod snapshot.

Supported text collisions expose a read-only diff editor. An administrator can choose any two source mods, swap sides, ignore whitespace, switch between side-by-side and unified layouts, search, keep only changes with context, and navigate change blocks. Inline spans highlight the exact changed characters. Paths are revalidated against managed snapshots before reading; binary content is rejected, files are capped at 2 MiB, and rendering is capped at 12,000 lines per side.

Compatibility has its own project tab. The dashboard only exposes a compact health summary and opens that tab without reloading the analysis. Batch recipes are deliberately narrow: they can disable mods with a verified missing target-version layout, disable entries with an unavailable source or effective `mod.info`, and apply the computed mod/map order. Every batch shows its exact targets, preserves snapshots, and leaves ambiguous file collisions for explicit review.

## Dependency-aware imports

Every local or Workshop import is preflighted before the project changes. The manager normalizes the `require=` Mod IDs found in `mod.info`, compares them with the current pack, and lists the missing dependencies in the application confirmation dialog. The administrator can add the selected mod with all resolvable dependencies or deliberately add only the selected mod.

Local dependencies are matched by exact Mod ID. For Workshop sources, PZASM also reads the item's official **Required Items** list; it never treats recommendations as dependencies. A one-click repair is available both on the missing-dependency diagnostic and on the affected mod card. A downloaded Workshop child is accepted only when its effective `mod.info` actually provides the requested Mod ID. If no verified source exists, the manager reports the unresolved ID instead of guessing. Added dependencies are placed before their requester and the complete order is validated again.

## Workshop discovery filters

The public Workshop browser combines Steam Community browse ordering with deterministic filtering of the public item details response. Search can target title and description together or either field alone. Multiple required and excluded tags are supported, with all/any matching for required tags. Additional filters cover publication/update age, creator SteamID64, current and lifetime subscriptions, favorites, views, minimum/maximum file size, preview/description availability, and whether the item is already present in the selected destination.

Search depth is explicit: one, three, or five Steam result pages are inspected per manager batch. Candidate IDs are deduplicated before the public details request, public details are fetched in batches, and browse results are cached briefly. Numeric and metadata filters are applied after Steam discovery so their behavior remains deterministic even when the public Workshop page ignores an optional URL parameter.
