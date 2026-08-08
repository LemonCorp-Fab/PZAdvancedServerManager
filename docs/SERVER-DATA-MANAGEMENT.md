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
- Restore creates a `pre-restore` backup of the current world before replacing it.
- Fresh start creates a `pre-reset` backup before removing the current world and player database.
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
pzasm server reset-world --name <profile> --yes [--json]
pzasm server delete-backup --name <profile> --backup <id> --yes
```

Use `--data-root <directory>` when the UI and CLI must share the same archive catalog. Destructive CLI operations require `--yes`.

## Remote servers

RCON can save, stop, restart, and administer Project Zomboid, but it cannot transfer world files. Consequently, the data panel does not present local file operations for an RCON-only remote profile. Use the VPS/provider snapshot system, run the PZASM CLI locally on the host with access to its `Zomboid` data directory, or configure a separately verified file-transfer workflow. The manager never guesses a remote filesystem path and never deletes remote data through RCON.

---

## Français

La gestion des données couvre le dossier du monde, la base des joueurs et ses fichiers annexes. Le serveur doit être arrêté. Une restauration ou un nouveau départ crée d'abord une sauvegarde de sécurité vérifiée par SHA-256. Un nouveau départ conserve l'INI, les mods et les SandboxVars. La configuration archivée n'est restaurée que sur demande explicite. RCON ne transporte pas de fichiers : pour un serveur distant, utilisez les snapshots du fournisseur ou exécutez la CLI sur l'hôte qui possède les données.

## Español

La gestión de datos incluye el mundo, la base de datos de jugadores y sus archivos auxiliares. El servidor debe estar detenido. Antes de restaurar o reiniciar el mundo se crea una copia de seguridad verificada con SHA-256. Un reinicio conserva el INI, los mods y SandboxVars. La configuración archivada solo se restaura si se solicita expresamente. RCON no transfiere archivos; para servidores remotos, use instantáneas del proveedor o ejecute la CLI en el host de datos.

## Deutsch

Die Datenverwaltung umfasst die Welt, die Spielerdatenbank und deren Begleitdateien. Der Server muss gestoppt sein. Vor Wiederherstellung oder Neustart der Welt wird automatisch eine SHA-256-geprüfte Sicherung erstellt. INI, Mods und SandboxVars bleiben bei einem Neustart erhalten. Archivierte Konfiguration wird nur auf ausdrücklichen Wunsch wiederhergestellt. RCON überträgt keine Dateien; verwenden Sie für entfernte Server Provider-Snapshots oder führen Sie die CLI auf dem Datenhost aus.

## Português (Brasil)

O gerenciamento inclui o mundo, o banco de jogadores e seus arquivos auxiliares. O servidor deve estar parado. Antes de restaurar ou reiniciar o mundo, o sistema cria um backup de segurança verificado por SHA-256. O reinício preserva INI, mods e SandboxVars. A configuração arquivada só é restaurada quando solicitada explicitamente. RCON não transfere arquivos; em servidores remotos, use snapshots do provedor ou execute a CLI no host dos dados.

## 简体中文

服务器数据管理涵盖世界目录、玩家数据库及其附属文件。执行操作前必须停止服务器。恢复或重新开档前会自动创建并用 SHA-256 校验安全备份。重新开档会保留 INI、模组列表和 SandboxVars；只有明确选择时才恢复存档中的配置。RCON 无法传输文件，因此远程服务器应使用服务商快照，或在能够访问数据目录的主机上运行 CLI。
