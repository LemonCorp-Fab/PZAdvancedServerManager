# PZ Advanced Server Manager

[English](README.md) · [Français](README.fr.md) · [Español](README.es.md) · [Deutsch](README.de.md) · [Português (Brasil)](README.pt-BR.md) · [简体中文](README.zh-CN.md)

PZ Advanced Server Manager (PZASM) ist ein lokaler Manager für Project Zomboid und den Dedicated Server. Eine zusammengehörige Mod-Sammlung wird über **eine einzige Workshop-ID** verteilt, sodass der Server das Paket statt jedes Quell-Items einzeln synchronisiert.

> Status: funktionsfähige Windows- und Linux-Version. Bundle, fixierte Snapshots, interner Workshop-Katalog, SteamCMD, eigenständige oder koordinierte Zeitplanung, Verbindungshinweis, Serververwaltung und Headless-CLI sind implementiert. Die erste Veröffentlichung sollte immer mit einem privaten Item getestet werden.

## Technisches Ergebnis

Ein Workshop-Item kann mehrere Ordner unter `mods/` enthalten, jeweils mit eigener `mod.info` und eigener `id=`:

```ini
WorkshopItems=EINDEUTIGE_PAKET_ID
Mods=ModIdA;ModIdB;ModIdC;PZASM_Notice_SUFFIX
```

Server und Clients vergleichen nur die Version des gemeinsamen Workshop-Items. Danach steuern die internen Mod-IDs das Laden. Die normalen Lua- und Prüfsummenprüfungen bleiben aktiv.

Der empfohlene Modus ist **Bundle** und behält ursprüngliche Ordner und Mod-IDs bei. **Strict Fusion** erzeugt eine Mod-ID, lehnt aber jede Kollision unterschiedlicher Dateien ab.

Siehe die vollständige [Architektur- und Machbarkeitsstudie](docs/ARCHITECTURE.de.md).

## Funktionen

- Erkennung von Spiel, Dedicated Server, Steam-Bibliotheken, SteamCMD und lokalen/Workshop-Mods;
- Unterstützung der Build-41/42-Strukturen und kompatibler Versionsordner;
- unabhängige, erneut öffnbare Projekte mit eigener GUID und Workshop-ID;
- private SHA-256-Snapshots zum exakten Fixieren der Quellversionen;
- Import per Workshop-ID und Ergänzung verfügbarer `require=`-Abhängigkeiten;
- interner Workshop-Katalog mit Suche, Sortierung, Tags, Vorschauen, Seitennavigation, direkter ID-Suche und seitenübergreifendem Auswahlkorb mit einzelnem Entfernen;
- gemeinsamer visueller Selektor für Packs sowie `WorkshopItems`/`Mods` lokaler oder dedizierter Server, bei weiterhin verfügbarer Rohbearbeitung;
- automatisch verwaltete portable SteamCMD-Installation direkt von Valve unter Windows und Linux beim ersten benötigten Vorgang, mit optionaler Vorbereitung über die Oberfläche oder `pzasm steamcmd install`;
- anonyme Downloads öffentlicher Workshop-Quellen, getrennt vom authentifizierten Herausgeberkonto;
- Bundle ohne Umschreiben von Manifesten, Lua, Skripten, Karten oder Assets;
- Strict Fusion mit Deduplizierung identischer Dateien und Konfliktbericht;
- vollständige Workshop-Beschreibung, öffentliches Manifest und Lockfile;
- Erfassung von Autoren, Lizenzen, Berechtigungen und nicht veröffentlichten privaten Nachweisen;
- rein informative Berechtigungsstatus und Warnungen, die Build, Veröffentlichung oder Automatisierung niemals blockieren; Kontrolle und Verantwortung bleiben beim Administrator;
- optionales, standardmäßig aktiviertes mehrsprachiges Verbindungsfenster mit vollständiger Liste, angegebenen Mod-Versionen, PZ-Profilen und fixierten Revisionen;
- Erstellung und spätere Aktualisierung desselben Workshop-Items;
- moderner responsiver Projektarbeitsbereich mit klareren Gruppen, standardmäßig eingeklappten Rechtekarten, sechs dauerhaft gewählten Sprachen sowie Hell-/Dunkelmodus (standardmäßig hell);
- detaillierter Workshop-Importfortschritt mit aktuellem Item, Phase, Zähler, Prozentwert, Analyseergebnis und behebbaren Fehlern;
- Kartenprioritäts-Assistent auf Basis von `map.info`, `lots=`-Abhängigkeiten, `.lotheader`-Zellkonflikten, Drag-and-drop und roher `Map=`-Bearbeitung;
- geführter Servereditor für Identität, Zugriff, RCON, Sitzung, Sicherungen und Inhalte plus vollständiger INI-Roheditor; beim lokalen Start wird die SQLite-Tabelle `whitelist` gelesen und das initiale `admin`-Passwort nur bei tatsächlich fehlendem Konto abgefragt;
- dynamische Wiedererkennung über `zombie.network.GameServer` und `-servername`, auch wenn der Server vor dem Manager gestartet wurde; `-coop`-Prozesse werden von Dedicated Servern unterschieden, der grafische Client allein wird ignoriert und doppelte Instanzen eines Profils werden als Konflikt gemeldet. Die Serveransicht mit Tabs bietet lesbare, durchsuchbare und nach Schweregrad filterbare Ausgaben aus `server-console.txt` oder `coop-console.txt`, begrenztes und bereinigtes stdout/stderr, Netzwerk, RCON und Befehls-/Antwortkonsole;
- detaillierter, abbrechbarer Fortschritt für Veröffentlichung, SteamCMD-Anmeldung und Mod-Aktualisierung mit Live-Ausgabe und Zeitlimit;
- lokale UI und Headless-CLI für Windows und Linux;
- `automation run`-Daemon mit prozessübergreifenden Sperren.

### Projektbefehle und Aktualisierungen

Erstellen, Mods aktualisieren und Veröffentlichen werden als primäre Projektbefehle hervorgehoben. Sensible Aktionen verwenden immer ein Bestätigungsfenster innerhalb der Oberfläche und niemals native Browserdialoge. Autor und Rechteinhaber werden, sofern vorhanden, aus der `mod.info` jeder Quelle vorausgefüllt und bleiben bearbeitbar. Jeder Mod kann von der globalen Aktualisierung ausgeschlossen und einzeln aktualisiert werden; sein Snapshot bleibt fixiert, bis eine einzelne Aktualisierung ausdrücklich angefordert wird.

## Start

Zum Kompilieren ist das [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0) erforderlich. Eigenständige CI-Artefakte benötigen keine installierte .NET-Laufzeit.

```powershell
Start-PZASM.cmd
```

```bash
chmod +x Start-PZASM.sh
./Start-PZASM.sh
```

Die UI lauscht lokal auf `http://localhost:5160`. Mit `--data-root <pfad>` verwenden UI und CLI dasselbe Datenverzeichnis.
SteamCMD wird bei der ersten Verwendung in den Manager-Ordner geladen, kontrolliert entpackt und initialisiert. Dashboard, „Distribution“ und CLI können es auch sofort vorbereiten oder neu installieren. Öffentliche Project-Zomboid-Quellen werden standardmäßig anonym geladen; nur die Veröffentlichung benötigt das Herausgeberkonto.

SteamCMD lädt bekannte Workshop-IDs, bietet aber keine vollständige Suche. Der interne Katalog liest öffentliche Steam-Community-Ergebnisse, ergänzt öffentliche Metadaten und übergibt erst die Auswahl an SteamCMD. Geplante Veröffentlichungen benötigen keinen lokalen Spielserver; die RCON-Koordination ist optional.

## Empfohlener Ablauf

1. Ein Projekt im Modus **Bundle** erstellen.
2. Erkannte Mods hinzufügen oder eine Workshop-ID importieren.
3. Autor und Berechtigung jeder Quelle dokumentieren.
4. Reihenfolge von Mods und Karten prüfen.
5. Bauen und `pack.lock.json` sowie `server-config.txt` kontrollieren.
6. SteamCMD automatisch vom Manager vorbereiten lassen (oder sofort unter „Distribution“ vorbereiten), das Herausgeberkonto konfigurieren, **Sitzung verbinden / erneuern** ausführen und zuerst privat veröffentlichen.
7. Vor der Produktion auf einem Staging-Server testen.

## Headless-CLI

```bash
dotnet run --project src/PZAdvancedServerManager.Cli -- scan
dotnet run --project src/PZAdvancedServerManager.Cli -- steamcmd install
dotnet run --project src/PZAdvancedServerManager.Cli -- steamcmd login --id <guid>
dotnet run --project src/PZAdvancedServerManager.Cli -- project create --name "Hauptserver"
dotnet run --project src/PZAdvancedServerManager.Cli -- project import-workshop --id <guid> --workshop-id 1234567890
dotnet run --project src/PZAdvancedServerManager.Cli -- project validate --id <guid>
dotnet run --project src/PZAdvancedServerManager.Cli -- project build --id <guid>
dotnet run --project src/PZAdvancedServerManager.Cli -- project publish --id <guid> --yes
dotnet run --project src/PZAdvancedServerManager.Cli -- automation run --interval 30
```

Jedes Projekt ist ein unabhängiges globales Paket. Ohne ausdrückliche Aktivierung durch den Administrator erfolgt keine automatische Aktualisierung. Beispielhafte systemd-Units liegen unter `deploy/systemd/`.

## Docker, Coolify und geschützter Zugriff

Der Produktionscontainer enthält Web-Manager, Zeitplaner, SSH-Client, SteamCMDs 32-Bit-Linux-Bibliotheken und die automatische SteamCMD-Installation. Alle Verwaltungsseiten erfordern ein Konto. Administratoren verwalten Benutzer und widerrufen Sitzungen; Operatoren verwalten Packs und Server ohne Benutzerverwaltung.

Unter Windows schützt `just docker-secret-setup` das Startpasswort und einen unabhängigen Datenschlüssel mit DPAPI außerhalb des Repositorys; RCON-Passwörter und API-Token werden mit AES-GCM verschlüsselt gespeichert. Unter Linux kann eine `.env`-Datei mit Modus `600` oder ein externer Secret-Manager verwendet werden. Hinterlege für Coolify `PZASM_ADMIN_PASSWORD` und einen stabilen `PZASM_DATA_ENCRYPTION_KEY` mit mindestens 32 zufälligen Zeichen als geschützte Variablen; Compose bindet beide als schreibgeschützte Secret-Dateien ein. Leite Port `5160` über HTTPS und behalte das Volume `pzasm-data` unbedingt bei. Siehe [Docker und Coolify](docs/DOCKER-COOLIFY.md).

## SteamCMD und entfernte Server

Pine Hosting besitzt ein eigenes API-Backend. API-Schlüssel und Server-ID genügen, um die gemeinsamen INI-, SandboxVars- und Lua-Editoren, Pack-Bereitstellung, Konsole, Prozesssteuerung und Provider-Backups ohne SSH zu verwenden. Wiederherstellung und Fresh Start erfordern einen gestoppten Server und bieten vorher ein Sicherheits-Backup an. Siehe [Pine Hosting provider](docs/PINE-HOSTING.md).

Steam-Passwort und Steam-Guard-Code werden nur für die jeweilige Anfrage über die Standardeingabe an SteamCMD gesendet; PZASM legt sie weder in die Befehlszeile noch speichert es sie. SteamCMD behält sein eigenes Token im portablen Ordner für geplante Veröffentlichungen. Ist die Sitzung abgelaufen oder fehlt ein Geheimnis, endet die Veröffentlichung sofort mit einer verständlichen Meldung, statt unsichtbar zu warten. Die Oberfläche zeigt die Live-Ausgabe und kann den externen Prozess abbrechen.

Ein entferntes Profil kann ausschließlich RCON verwenden: authentifizierter Status, Konsole, `save`, `quit` und Koordination funktionieren ohne SSH. Wenn systemd, Docker, ein Panel oder der Hoster Project Zomboid nach `quit` neu startet, veröffentlicht PZASM zuerst und fordert danach den sauberen RCON-Neustart an. SSH bleibt optional für INI-Zugriff oder einen ausdrücklichen Startbefehl des Spiels. PZASM startet niemals den gesamten VPS oder Dedicated Host neu.

## Rechte und Verantwortung

PZASM gewährt keine Rechte an enthaltenen Mods. Die [offizielle Modding-Richtlinie](https://projectzomboid.com/blog/modding-policy/) verlangt passende Genehmigungen und eine vollständige Quellenliste für öffentliche und nicht gelistete Pakete. Zusätzlich gilt die [Steam-Workshop-Vereinbarung](https://steamcommunity.com/workshop/workshopsubmitinfo/).

Ersteller und Herausgeber des Pakets tragen allein die Verantwortung für Genehmigungen, Lizenzen, Namensnennungen und Inhalte Dritter. LemonCorp und PZASM-Mitwirkende haften nicht für von Benutzern erstellte oder veröffentlichte Pakete.

## Entwicklung

Das Repository enthält ein plattformübergreifendes `Justfile`. Nach der Installation von [just](https://github.com/casey/just) stehen unter anderem diese Befehle bereit:

```text
just                 # alle Rezepte anzeigen
just check           # Formatierung, Release-Build und Tests prüfen
just build           # die gesamte Solution bauen
just test            # alle Tests ausführen
just run-ui           # UI starten und Browser öffnen
just run-cli help     # einen CLI-Befehl ausführen
just automation      # Headless-Scheduler starten
just publish          # für das aktuelle System veröffentlichen
just publish-all      # win-x64 und linux-x64 veröffentlichen
```

Mit `CONFIGURATION` und `PUBLISH_DIR` lassen sich die Vorgaben `Release` und `publish` überschreiben. Die Rezepte akzeptieren außerdem zusätzliche Argumente.

```powershell
dotnet restore
dotnet test PZAdvancedServerManager.sln
dotnet publish src/PZAdvancedServerManager.App -c Release -o publish
```

Der PZASM-Port darf nicht öffentlich ins Internet gestellt werden. Die Oberfläche ist ein lokales Verwaltungswerkzeug ohne Netzwerkauthentifizierung.
