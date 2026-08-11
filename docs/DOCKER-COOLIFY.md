# Docker and Coolify deployment

The production image runs the complete web manager, scheduler, SteamCMD, Workshop downloads and publishing, SSH, RCON, and provider API backends. It uses a non-root Linux user and stores every durable file below `/data`.

## Security model

Every manager page is private by default. The only anonymous application pages are login, one-time first setup, access denied, error handling, and the two health endpoints.

- Passwords are hashed by ASP.NET Core Identity and are never stored as clear text.
- Five failed logins lock a user for 15 minutes.
- Session cookies are `HttpOnly`, use `SameSite=Lax`, and become secure cookies when Coolify terminates HTTPS and forwards the original scheme.
- Proxy headers are trusted only when `PZASM_TRUST_PROXY_HEADERS=true`; the production Compose file enables this for Coolify, while native local runs ignore forwarded headers.
- Authentication encryption keys are persisted in `/data/identity/keys`, so valid sessions survive a container replacement.
- Administrators can create operators or other administrators, reset passwords, disable accounts, and revoke active sessions.
- Operators can use packs and servers but cannot manage manager accounts.
- The final active administrator cannot be disabled or demoted.
- Stored RCON passwords and provider API tokens are encrypted with AES-GCM before they enter profile JSON. Existing plaintext profiles are migrated atomically on their first read.

The Compose deployment creates the first administrator from `PZASM_ADMIN_USERNAME`, `PZASM_ADMIN_PASSWORD`, and `PZASM_ADMIN_DISPLAY_NAME`. These values are read only while the user database is empty. Compose mounts the password as a read-only secret file instead of exposing it in the container environment. Set `PZASM_ADMIN_PASSWORD` as a protected Coolify secret; the application receives only `PZASM_ADMIN_PASSWORD_FILE=/run/secrets/pzasm_admin_password`. A non-container installation can provide either variable, or use the one-time setup page until the first account is created.

`PZASM_DATA_ENCRYPTION_KEY` is a separate, stable deployment secret used to encrypt unattended RCON and provider credentials. Compose mounts it at `/run/secrets/pzasm_data_encryption_key`; it is never placed in the container environment. Generate at least 32 random characters, store it outside the data volume, include it in the deployment backup procedure, and do not rotate it without first decrypting or re-saving the profiles with a supported migration. Losing this key makes the encrypted credentials unrecoverable.

## Persistent data

The named volume `pzasm-data` is mounted at `/data` and contains:

- user accounts and persisted cookie encryption keys;
- pack projects, source snapshots, builds, previews, and operation logs;
- server profiles, provider state, and manager backups;
- SteamCMD itself, `config/config.vdf`, the portable Steam session, Workshop manifests, and downloaded Workshop content.
- temporary transfer workspaces below `/data/transfers`; completed operations remove them immediately and the manager reclaims abandoned manager-owned transfer files after six hours.

Back up this volume before moving or rebuilding the deployment. Do not mount `/data` as a temporary volume. If a bind mount is used instead of the named volume, its directory must be writable by UID/GID `10001`.

## Local Docker Compose

### Windows with Docker Desktop

Do not keep deployment secrets in a plaintext `.env` file. The Windows wrapper stores both the administrator password and an independently generated data-encryption key as DPAPI-encrypted values tied to the current Windows account, then restricts their file ACLs to that account and `SYSTEM`. It decrypts them only in the wrapper process, passes them to Compose as environment-backed secrets, and removes them from the process environment when the command finishes.

```powershell
just docker-secret-setup
just docker-up
curl http://127.0.0.1:5160/health/ready
```

The encrypted values are stored outside the repository below `%LOCALAPPDATA%\LemonCorp\PZAdvancedServerManager\secrets`. Docker grants only the manager service access and mounts the resulting secrets read-only below `/run/secrets`. Windows administrators and code already executing as your Windows account remain inside the trust boundary; no local secret system can protect against a fully compromised account.

### Linux

```bash
cp .env.example .env
# Set a long, unique PZASM_ADMIN_PASSWORD and an independent random
# PZASM_DATA_ENCRYPTION_KEY containing at least 32 characters.
chmod 600 .env
docker compose -f compose.yaml -f compose.local.yaml up -d --build
docker compose -f compose.yaml -f compose.local.yaml ps
curl http://127.0.0.1:5160/health/ready
```

The default host binding is `127.0.0.1:5160`. Put it behind an HTTPS reverse proxy for network access. To expose another local port, set `PZASM_HTTP_PORT`. Avoid setting `PZASM_BIND_ADDRESS=0.0.0.0` unless a firewall and HTTPS proxy protect the port.

The Linux `.env` fallback is plaintext at rest even with mode `600`; use an external secret manager or an equivalent wrapper when the host's threat model requires encrypted local storage.

Useful commands:

```bash
docker compose -f compose.yaml -f compose.local.yaml logs -f manager
docker compose -f compose.yaml -f compose.local.yaml restart manager
docker compose -f compose.yaml -f compose.local.yaml down              # keeps pzasm-data
docker compose -f compose.yaml -f compose.local.yaml down --volumes    # destructive: deletes all manager data
```

The headless CLI is included in the same image and shares `/data` with the UI:

```bash
docker compose -f compose.yaml -f compose.local.yaml exec manager \
  dotnet /app/cli/pzasm.dll projects --data-root /data
```

## Coolify

1. Create a resource from this Git repository and select **Docker Compose**.
2. Use only `compose.yaml`. It pulls the public `ghcr.io/lemoncorp-fab/pzadvancedservermanager` image and exposes the `manager` service on the internal container port `5160` without bypassing Coolify's proxy through a host port. Set `PZASM_IMAGE_TAG` to a release such as `0.2.0` when you want immutable deployments; the default is `latest`.
3. Add `PZASM_ADMIN_PASSWORD` and an independent, stable `PZASM_DATA_ENCRYPTION_KEY` of at least 32 random characters as required protected variables. Compose converts them to read-only files mounted in the container. Optionally set `PZASM_ADMIN_USERNAME` and `PZASM_ADMIN_DISPLAY_NAME`.
4. Assign an HTTPS domain to port `5160`. Do not remove the `pzasm-data` volume.
5. Deploy and wait for `/health/ready` to report `status: ready`.
6. Sign in, open the SteamCMD section, and verify its status. Automatic installation is enabled; the same action in the UI can retry a failed download.

`/health/live` proves that the web process responds. `/health/ready` also verifies the Identity database and reports `steamCmd` as `installed` or `not-installed`. A missing SteamCMD does not make the web manager unhealthy, because installation can be retried without replacing the container.

## Large portable transfers and disk usage

The default `.pzasm-pack` export is configuration-only: it excludes downloaded mods and the generated build, stays small, and marks the imported project as requiring a Workshop download before build or publication. Use the explicit complete mode only when the destination must receive byte-identical frozen revisions or local mods. In complete archives, identical source, asset, snapshot, and build files are represented by one SHA-256-addressed blob. Imports verify every blob before changing an existing project and use hard links when the destination filesystem supports them. Server connection exports use a small password-encrypted `.pzasm-servers` file.

The manager rejects pack archives above its documented safety limits and checks available disk space before materializing an import. Atomic replacement temporarily needs room for the incoming verified data while the previous project remains recoverable. Keep enough free space for the archive, its unique extracted blobs, and any files that cannot be hard-linked. Successful and failed operations remove their transaction workspace; the background cleaner handles remnants left by a process or container crash and skips workspaces that are still locked by an active operation. It also reclaims abandoned build/source transactions and installer files, caps inactive SteamCMD logs, and removes unreferenced Workshop download caches after seven days.

Browser uploads also pass through Coolify's reverse proxy. Increase the proxy request-body limit and timeout when importing multi-gigabyte packs; otherwise the proxy can reject or interrupt the request before the manager receives it. For very large archives, mount or copy the file into storage visible to the container and use the included CLI to avoid HTTP proxy buffering:

```bash
dotnet /app/cli/pzasm.dll project import \
  --file /imports/my-pack.pzasm-pack \
  --data-root /data
```

Pack exports deliberately exclude the Steam login session and rebind SteamCMD to the destination manager. Server connection files are encrypted with their separate transfer password, while pack archives are not encrypted because their mod sources and permission evidence must remain portable. Protect pack archives accordingly.

## SteamCMD in the container

The Linux image includes SteamCMD's required 32-bit GCC and C++ libraries. On the first deployment, the manager downloads Valve's official Linux archive into `/data/tools/steamcmd`, extracts it safely, marks `steamcmd.sh` executable, and performs a bootstrap run. Later containers reuse that installation.

Public Project Zomboid Workshop sources and the dedicated server AppID can use anonymous login. Publishing still requires a Steam account that owns Project Zomboid and owns the Workshop item. Steam Guard interaction is handled by the existing manager flow; SteamCMD's portable session remains in the persistent volume. Treat the volume as sensitive and never publish it as an image layer or back it up to an untrusted location.

The image is pinned to `linux/amd64` because SteamCMD's Linux client depends on x86 compatibility. An ARM-only VPS needs x86 emulation and is not a recommended production target.

## Server control boundaries

Remote RCON, SSH, Pine Hosting, scheduled pack publication, and coordinated restarts work normally from the container. The SSH client is included in the image.

A container cannot safely discover or control unrelated Project Zomboid processes running directly on the Docker host. The deployment intentionally does not mount the Docker socket or use host PID mode. Use a remote server profile with RCON, SSH, or a provider backend for those servers. Local dedicated-process discovery remains available in native Windows/Linux installations of the manager.

## Updates and recovery

Deploy a new `PZASM_IMAGE_TAG` while retaining `pzasm-data`. Database, sessions, SteamCMD, and Workshop caches are independent of the image. If a release fails, roll back the image tag without rolling back or deleting the volume. For disaster recovery, restore a consistent backup of the entire volume rather than only individual JSON files.
