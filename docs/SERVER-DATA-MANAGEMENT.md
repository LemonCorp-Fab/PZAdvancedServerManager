# Server data management

PZ Advanced Server Manager can back up, restore, or reset the world data of a local Project Zomboid server profile. These operations are available in the server workspace and in the Windows/Linux CLI.

## Managed data

For a profile named `servertest`, the manager controls only these conventional Project Zomboid paths below the detected `Zomboid` user directory:

- `Saves/Multiplayer/servertest/`: world chunks, map state, vehicles, and other world files;
- `db/servertest.db` and its `-wal`, `-shm`, and `-journal` sidecars: player and multiplayer database state;
- recovery copies of the profile INI, `SandboxVars`, spawn regions, and spawn points files.

Archives are stored outside the game data in the manager data root under `server-data/<profile>/backups`. Each backup is a ZIP archive accompanied by metadata and a SHA-256 digest. The archive is rejected at restore time if its digest no longer matches.

## Safety rules

- The game server must be stopped before backup, restore, or fresh start. This prevents an inconsistent archive and file-lock races.
- When a local dedicated process is alive but RCON is unavailable, the manager exposes a last-resort force-stop action. It re-discovers the exact non-`-coop` `GameServer` process for the selected profile, displays its Java PID, requires explicit confirmation, terminates only that process tree, and verifies its disappearance. No `save` command can be sent in this state, so recent world changes may be lost or inconsistent.
- Restore creates a `pre-restore` backup of the current world before replacing it.
- Fresh start offers a `pre-reset` backup option before removing the current world and player database. It is enabled by default but can be explicitly disabled by the administrator.
- Fresh start preserves the server INI, mod lists, SandboxVars, spawn configuration, and manager profile.
- Restore preserves the current configuration by default. Configuration recovery files are restored only when the administrator explicitly selects that option.
- Extraction rejects absolute paths, parent traversal, reparse points, and files outside the selected profile scope.
- Cancellation is honored before the destructive replacement stage. Replacement uses staging and rollback paths to reduce partial-state risk.
- Deleting a manager archive is separate from resetting the world and requires explicit confirmation.

## CLI

```text
pzasm server data-status --name <profile> [--json]
pzasm server backup --name <profile> [--json]
pzasm server backups --name <profile> [--json]
pzasm server restore --name <profile> --backup <id> [--restore-config] --yes [--json]
pzasm server reset-world --name <profile> --yes [--no-backup] [--json]
pzasm server delete-backup --name <profile> --backup <id> --yes
```

`data-status` also reports the initial administrator-account state. PZASM opens the profile's player database read-only and checks the `whitelist` table for the `admin` account. The local UI requires an initial password only when that row is missing. If the database cannot be read safely, startup remains possible without a password; Project Zomboid's own prompt is then treated as the authoritative fallback.

Use `--data-root <directory>` when the UI and CLI must share the same archive catalog. Destructive CLI operations require `--yes`.
Passing `--no-backup` with `reset-world` explicitly accepts that the removed world cannot be recovered from PZASM unless another archive already exists.

## First start after a fresh start

Removing the player database also removes Project Zomboid's built-in `admin` account. On the next launch, the game requests a new administrator password from its console. The local server start card therefore checks for the `admin` row in SQLite and exposes two transient confirmation fields only when that account is missing. World-folder presence is deliberately not used as a proxy.

The manager waits for Project Zomboid's actual password prompt and then writes the value to the process standard input. The value is not persisted, included in the Java or shell command line, or written to manager logs. It is ignored when the existing database does not request administrator initialization.

For headless startup, use exactly one of the following:

```text
pzasm server start --name <profile> --admin-password-file <file>
pzasm server start --name <profile> --admin-password-env <environment-variable>
pzasm server start --name <profile> --admin-password <value>
```

The file or environment-variable forms are recommended because a direct command-line value may remain visible in shell history. PZASM still passes the resulting value to the game through standard input only.

## Remote servers

RCON can save, stop, restart, and administer Project Zomboid, but it cannot transfer world files. Consequently, the data panel does not present local file operations for an RCON-only remote profile. Use the VPS/provider snapshot system, run the PZASM CLI locally on the host with access to its `Zomboid` data directory, or configure a separately verified file-transfer workflow. The manager never guesses a remote filesystem path and never deletes remote data through RCON.

---

## Français

La gestion des données couvre le dossier du monde, la base des joueurs et ses fichiers annexes. Le serveur doit être arrêté. Une restauration crée toujours une sauvegarde de sécurité vérifiée par SHA-256. Pour un nouveau départ, cette sauvegarde est proposée et activée par défaut, mais l’administrateur peut la désactiver explicitement. L'INI, les mods et les SandboxVars sont conservés. Au démarrage local, PZASM lit `whitelist` dans SQLite et ne demande le mot de passe initial que si le compte `admin` manque réellement ; la présence du monde n’est pas utilisée comme approximation. RCON ne transporte pas de fichiers : pour un serveur distant, utilisez les snapshots du fournisseur ou exécutez la CLI sur l'hôte qui possède les données.

Si un serveur dédié local reste actif sans RCON, l’arrêt forcé de dernier recours cible uniquement le processus `GameServer` non-`-coop` du profil, affiche son PID Java, exige une confirmation explicite et vérifie sa disparition. Sans commande `save`, les dernières données peuvent être perdues ou incohérentes.

## Español

La gestión de datos incluye el mundo, la base de datos de jugadores y sus archivos auxiliares. El servidor debe estar detenido. Una restauración siempre crea una copia de seguridad verificada con SHA-256. Para un mundo nuevo, la copia está activada por defecto, pero el administrador puede desactivarla expresamente. Se conservan el INI, los mods y SandboxVars. Al iniciar localmente, PZASM lee `whitelist` en SQLite y solo solicita la contraseña inicial si realmente falta la cuenta `admin`; no usa la presencia del mundo como aproximación. RCON no transfiere archivos; para servidores remotos, use instantáneas del proveedor o ejecute la CLI en el host de datos.

Si un servidor dedicado local sigue activo sin RCON, la parada forzada de último recurso apunta únicamente al proceso `GameServer` no `-coop` del perfil, muestra su PID Java, exige confirmación explícita y verifica que desaparezca. Sin el comando `save`, los últimos datos pueden perderse o quedar incoherentes.

## Deutsch

Die Datenverwaltung umfasst die Welt, die Spielerdatenbank und deren Begleitdateien. Der Server muss gestoppt sein. Eine Wiederherstellung erstellt immer eine SHA-256-geprüfte Sicherung. Bei einem Fresh Start ist die Sicherung standardmäßig aktiviert, kann aber ausdrücklich deaktiviert werden. INI, Mods und SandboxVars bleiben erhalten. Beim lokalen Start liest PZASM `whitelist` aus SQLite und fragt das initiale Passwort nur ab, wenn das `admin`-Konto tatsächlich fehlt; die Weltpräsenz dient nicht als Näherung. RCON überträgt keine Dateien; verwenden Sie für entfernte Server Provider-Snapshots oder führen Sie die CLI auf dem Datenhost aus.

Bleibt ein lokaler Dedicated Server ohne RCON aktiv, richtet sich der erzwungene Notstopp ausschließlich gegen den nicht-`-coop`-`GameServer`-Prozess des Profils, zeigt dessen Java-PID, verlangt eine ausdrückliche Bestätigung und prüft das Prozessende. Ohne `save` können die neuesten Daten verloren gehen oder inkonsistent sein.

## Português (Brasil)

O gerenciamento inclui o mundo, o banco de jogadores e seus arquivos auxiliares. O servidor deve estar parado. Uma restauração sempre cria um backup de segurança verificado por SHA-256. No fresh start, o backup vem ativado por padrão, mas pode ser desativado explicitamente. INI, mods e SandboxVars são preservados. Na inicialização local, o PZASM lê `whitelist` no SQLite e só solicita a senha inicial quando a conta `admin` realmente não existe; a presença do mundo não é usada como aproximação. RCON não transfere arquivos; em servidores remotos, use snapshots do provedor ou execute a CLI no host dos dados.

Se um servidor dedicado local continuar ativo sem RCON, a parada forçada de último recurso mira somente o processo `GameServer` não `-coop` do perfil, mostra seu PID Java, exige confirmação explícita e verifica o encerramento. Sem o comando `save`, os dados mais recentes podem ser perdidos ou ficar inconsistentes.

## 简体中文

服务器数据管理涵盖世界目录、玩家数据库及其附属文件。执行操作前必须停止服务器。恢复操作始终会创建并用 SHA-256 校验安全备份。重新开档时备份选项默认启用，但管理员可以明确关闭；INI、模组列表和 SandboxVars 会保留。本地启动时，PZASM 会读取 SQLite 中的 `whitelist`，仅在确实缺少 `admin` 账户时要求初始密码，不再以世界目录是否存在作为替代判断。RCON 无法传输文件，因此远程服务器应使用服务商快照，或在能够访问数据目录的主机上运行 CLI。

如果本地独立服务器进程仍在运行但 RCON 不可用，最终手段的强制停止只会定位该配置对应的非 `-coop` `GameServer` 进程，显示其 Java PID，要求明确确认，并验证进程已经消失。由于无法发送 `save`，最近的世界数据可能丢失或不一致。
