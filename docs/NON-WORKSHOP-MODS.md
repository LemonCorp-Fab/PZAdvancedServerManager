# Non-Workshop mods on Project Zomboid servers

## Technical verdict

Project Zomboid can load a mod that is installed manually in the server's `Zomboid/mods` directory and listed in `Mods=` without a matching entry in `WorkshopItems=`. This is only a server-side loading mechanism: the dedicated server does not transfer missing mod files to connecting players.

Every client must therefore install the exact same files manually before connecting. A local-only pack cannot provide the automatic acquisition and version synchronization that Project Zomboid performs for Steam Workshop items. PZ Advanced Server Manager consequently requires a published Workshop ID before offering a pack for automatic server application.

This distinction is confirmed by The Indie Stone support guidance:

- [Workshop or Mod on Linux server](https://theindiestone.com/forums/topic/16559-workshop-or-mod-on-linux-server/)
- [How to add mods to a multiplayer server](https://theindiestone.com/forums/topic/12554-how-to-add-mods-to-a-mp-server/)
- [Request to let clients download required mod files from the host](https://theindiestone.com/forums/topic/57967-let-clients-download-required-mod-files-from-host/)

## Supported workflows

### Workshop distribution

1. Build the pack.
2. Publish it and retain its Workshop ID.
3. Apply the pack to the server profile.
4. Restart the Project Zomboid process at the administrator's chosen time.

Clients download the single Workshop item through Steam. The internal Mod IDs remain listed in `Mods=` and are loaded from that one Workshop content directory.

### Manual distribution

Manual deployment remains technically possible for private environments, but it is deliberately not presented as automatic client distribution:

1. Copy the exact pack files to the server's `Zomboid/mods` directory.
2. Copy the exact same files to every player's `Zomboid/mods` directory.
3. Keep `WorkshopItems=` empty for that content and list the internal IDs in `Mods=`.
4. Coordinate every future update outside the game.

This mode has no built-in client download, no Workshop ownership check, and no reliable remediation when one player has different files.

## Résumé français

Le serveur peut charger un mod local depuis `Zomboid/mods`, mais il ne l'envoie pas aux joueurs. Chaque client doit installer manuellement les mêmes fichiers. La publication Workshop reste donc obligatoire dans le manager pour bénéficier du téléchargement automatique et d'une version commune.

## Resumen español

El servidor puede cargar un mod local desde `Zomboid/mods`, pero no lo distribuye a los jugadores. Cada cliente debe instalar manualmente los mismos archivos. Por eso el gestor exige una publicación en Workshop para la descarga automática y una versión común.

## Deutsche Zusammenfassung

Der Server kann einen lokalen Mod aus `Zomboid/mods` laden, überträgt ihn aber nicht an Spieler. Jeder Client muss exakt dieselben Dateien manuell installieren. Für automatischen Download und eine gemeinsame Version verlangt der Manager daher eine Workshop-Veröffentlichung.

## Resumo em português

O servidor pode carregar um mod local de `Zomboid/mods`, mas não o envia aos jogadores. Cada cliente precisa instalar manualmente os mesmos arquivos. Por isso, o gerenciador exige publicação no Workshop para download automático e versão comum.

## 中文摘要

服务器可以从 `Zomboid/mods` 加载本地模组，但不会把文件传输给玩家。每个客户端都必须手动安装完全相同的文件。因此，要实现自动下载并确保版本一致，管理器要求先发布到 Steam 创意工坊。
