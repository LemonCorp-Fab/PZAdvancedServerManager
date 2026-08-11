# Portable transfers

PZ Advanced Server Manager supports two versioned transfer formats:

- `.pzasm-pack` transfers one pack configuration or one complete frozen pack between administrators;
- `.pzasm-servers` transfers one or more remote-server connections.

## Lightweight pack archives

The default export is configuration-only. It preserves the project GUID and stable suffix, Workshop ID, project timestamps, publication state, mod references and order, dependency data, conflict decisions, maps, permission records, private permission attachments, custom preview, and automation settings. It deliberately excludes downloaded mod sources and the generated Workshop build, so even a pack containing hundreds of mods remains small.

After import, the project is explicitly marked as requiring source hydration. Build, publishing, and automation validation remain blocked until **Update mods** downloads every active Workshop item and recreates its private pinned snapshot. This initial hydration includes active mods normally excluded from global updates, because no usable source exists yet. Once hydration succeeds, their normal update policies apply again.

A lightweight transfer cannot reproduce an old Workshop revision after its author publishes a newer one, and it cannot carry local mods without a Workshop ID. Use a complete archive when byte-identical frozen versions are required. Configuration-only export refuses projects containing local mod entries instead of silently producing an unrecoverable transfer.

## Complete pack archives

A pack archive preserves the project GUID and stable suffix, Workshop ID, project timestamps, publication state, mod references and order, dependency data, conflict decisions, maps, permission records, private permission attachments, custom preview, pinned source snapshots, project assets, automation settings, and the current build directory.

Source snapshots and the Workshop build frequently contain the same bytes. The archive stores content-addressed SHA-256 blobs, so identical files are written once and referenced from both logical trees. Import verifies every blob before changing an existing project. The new sources, assets, build, and project JSON are staged first and committed together; explicit replacement is required when the same project GUID already exists. VDF and local snapshot paths are rebased to the destination manager.

SteamCMD binaries and cached Steam authentication are machine-bound and are not exported. A configured SteamCMD path is rebound to the destination manager installation, and Steam authentication must be established on the destination. Private permission attachments are included in the pack archive without archive-level encryption, so the `.pzasm-pack` file must be stored and shared securely.

Safety limits are 500,000 logical files, 64 GiB of unique blob data, and 128 GiB of reconstructed logical data. Free space is checked before extraction. When the filesystem supports hard links, validated blobs, snapshots, and build files share disk blocks whenever their metadata is compatible. Atomic replacement temporarily retains the previous project until commit succeeds.

## Encrypted server-connection archives

Remote connection exports preserve stable IDs, provider details, Pine Hosting API credentials, RCON credentials, SSH settings, commands, and timestamps. Existing SSH private-key files are included by default. The payload is encrypted with AES-256-GCM; its key is derived from a separate transfer password using PBKDF2-HMAC-SHA256 with a random 256-bit salt and 600,000 iterations.

On import, authenticated decryption happens in memory. SSH keys are integrity-checked and installed with owner-only permissions on Unix. API and RCON secrets are immediately encrypted again with the destination manager's local data-encryption key before the connection store is written. A wrong password, modified payload, duplicate identity, local-profile name collision, or existing remote connection is rejected before mutation unless explicit remote replacement was requested.

Use a transfer password of at least 12 characters and exchange it through a channel separate from the archive. The CLI supports password files and environment variables so unattended jobs do not need to expose a password in the process command line.

## Disk lifecycle

Successful UI downloads use delete-on-close files. CLI exports move the generated file to the requested destination. Failed exports delete partial output. Import staging and rollback directories are removed after success or failure. A bounded cleanup worker scans only manager-owned transfer names every hour and removes abandoned artifacts older than six hours, covering process crashes and interrupted container restarts without touching user files. Active long-running transfers hold an exclusive lease and are skipped by cleanup.

General storage maintenance also removes abandoned build/source transactions, installer downloads, restore staging, temporary writes, and Windows launcher scripts; trims exclusively accessible SteamCMD logs after 64 MiB while retaining the newest 8 MiB; and removes unreferenced SteamCMD Workshop cache entries after seven days. Pinned project snapshots, current builds, user-created world backups, referenced Workshop caches, Steam sessions, and unknown user files are never treated as disposable. Local, SSH, and Pine Hosting configuration backups retain the newest 20 revisions per file when the remote backend exposes the required file-listing tools.

For large browser imports behind a reverse proxy, ensure the proxy accepts the archive size. The application accepts up to 65 GiB of multipart request data, but an upstream proxy can impose a lower limit. The CLI can import an archive directly from a mounted path and avoids HTTP buffering:

```bash
pzasm project export --id <guid> --file /exports/pack.pzasm-pack
pzasm project export --id <guid> --file /exports/pack-complete.pzasm-pack --complete
pzasm project import --file /exports/pack.pzasm-pack
pzasm server export-connections --file /exports/servers.pzasm-servers --transfer-password-file /run/secrets/transfer-password
pzasm server import-connections --file /exports/servers.pzasm-servers --transfer-password-file /run/secrets/transfer-password
```
