# Pine Hosting provider

PZ Advanced Server Manager includes a dedicated Pine Hosting backend. It uses Pine's client API, which follows the Pterodactyl client API contract, instead of automating the hosting panel in a browser.

## Credentials

Create an API key in the Pine Hosting panel and copy the short server identifier shown by the panel. A profile requires only:

- the API key;
- the server identifier;
- the Project Zomboid INI path, which defaults to `/.cache/Server/Zomboid.ini`.

The API endpoint is fixed to `https://panel.pinehosting.com`. PZASM sends the key as an `Authorization: Bearer` header and never places it in a URL, page output, operation log, or console history. The edit form never returns the stored key to the browser; leaving the field empty preserves the current value. Protect the local PZASM data directory because unattended operation requires the provider key to remain available there.

## Shared configuration pipeline

Pine is a transport and control provider, not a separate configuration editor. The same `ServerConfigDocument`, structured INI catalog, SandboxVars parser, Lua validation, package application, read-after-write verification, and offline checks are used for local, SSH, and Pine profiles.

The remote backend router selects one implementation:

- generic VPS: SSH file I/O plus RCON control;
- Pine Hosting: API file I/O, console commands, power states, resources, and provider backups.

Before changing a Pine text file, PZASM reads the current content and writes a timestamped `.pzasm.*.bak` file through the provider API. It then writes the requested content and reads it back. A mismatch is reported as a failed operation, not as a successful save.

## Controls and backups

Pine profiles support start, graceful stop, restart, console commands, runtime state, full provider backups, download links, backup locking, restore, and fresh start. Graceful stop sends `save` and `quit` before using the provider stop signal if the game does not exit within the bounded wait. Restart sends `save` before the provider restart signal.

PZASM refuses configuration writes, restore, fresh start, and consistent backup creation while the provider reports a running or transitional server. A Pine fresh start can create and verify a locked provider backup first. It then deletes only these allowlisted Project Zomboid targets:

- `/.cache/Saves/Multiplayer/Zomboid`;
- `/.cache/db/Zomboid.db`.

These are paths inside the provider file API jail; Pine exposes the same locations as `/home/container/.cache/...` over its container/SFTP view.

It does not delete the INI, SandboxVars, Workshop configuration, mods, or unrelated container files.

## CLI

```bash
pzasm server create-remote --provider pine --name production --api-key-env PINE_API_KEY --server-id a1b2c3d4
pzasm server status --name production
pzasm server set --name production --key MaxPlayers --value 32 --yes
pzasm server backup --name production --lock
pzasm server backups --name production --json
pzasm server restore --name production --backup <uuid> --yes
pzasm server reset-world --name production --yes
```

Prefer `--api-key-env` or `--api-key-file` when invoking the CLI so the API key is not retained in shell history. UI entry uses a password field.

## API scope and limitations

The implementation uses the client API routes for server details, resources, files, commands, power, and backups. It does not require a Pine web-session cookie and does not submit the Pine account password. RCON can still be configured as an optional secondary channel, but it is not required for a Pine profile.

PZASM cannot create a missing Project Zomboid INI safely without knowing whether Pine has completed the first server initialization. Start the server once from Pine if the configured INI does not exist, then reconnect the profile. Backup restore completion is finalized by the provider; keep the server stopped and verify the panel state before starting it again.
