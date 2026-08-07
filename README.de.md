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
- portable Ein-Klick-Installation von SteamCMD direkt von Valve unter Windows und Linux, auch mit `pzasm steamcmd install`;
- anonyme Downloads öffentlicher Workshop-Quellen, getrennt vom authentifizierten Herausgeberkonto;
- Bundle ohne Umschreiben von Manifesten, Lua, Skripten, Karten oder Assets;
- Strict Fusion mit Deduplizierung identischer Dateien und Konfliktbericht;
- vollständige Workshop-Beschreibung, öffentliches Manifest und Lockfile;
- Erfassung von Autoren, Lizenzen, Berechtigungen und nicht veröffentlichten privaten Nachweisen;
- optionales, standardmäßig aktiviertes mehrsprachiges Verbindungsfenster mit vollständiger Liste, angegebenen Mod-Versionen, PZ-Profilen und fixierten Revisionen;
- Erstellung und spätere Aktualisierung desselben Workshop-Items;
- moderner responsiver Projektarbeitsbereich mit klareren Gruppen, standardmäßig eingeklappten Rechtekarten, sechs dauerhaft gewählten Sprachen sowie Hell-/Dunkelmodus (standardmäßig hell);
- detaillierter Workshop-Importfortschritt mit aktuellem Item, Phase, Zähler, Prozentwert, Analyseergebnis und behebbaren Fehlern;
- Kartenprioritäts-Assistent auf Basis von `map.info`, `lots=`-Abhängigkeiten, `.lotheader`-Zellkonflikten, Drag-and-drop und roher `Map=`-Bearbeitung;
- geführter Servereditor für Identität, Zugriff, RCON, Sitzung, Sicherungen und Inhalte plus vollständiger INI-Roheditor;
- geordnetes RCON-`save`/`quit` und koordinierter Neustart;
- lokale UI und Headless-CLI für Windows und Linux;
- `automation run`-Daemon mit prozessübergreifenden Sperren.

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
SteamCMD lässt sich im Dashboard oder im Tab „Distribution“ installieren. Öffentliche Project-Zomboid-Quellen werden standardmäßig anonym geladen; nur die Veröffentlichung benötigt das Herausgeberkonto.

SteamCMD lädt bekannte Workshop-IDs, bietet aber keine vollständige Suche. Der interne Katalog liest öffentliche Steam-Community-Ergebnisse, ergänzt öffentliche Metadaten und übergibt erst die Auswahl an SteamCMD. Geplante Veröffentlichungen benötigen keinen lokalen Spielserver; die RCON-Koordination ist optional.

## Empfohlener Ablauf

1. Ein Projekt im Modus **Bundle** erstellen.
2. Erkannte Mods hinzufügen oder eine Workshop-ID importieren.
3. Autor und Berechtigung jeder Quelle dokumentieren.
4. Reihenfolge von Mods und Karten prüfen.
5. Bauen und `pack.lock.json` sowie `server-config.txt` kontrollieren.
6. SteamCMD mit einem Klick installieren, das Herausgeberkonto konfigurieren und zuerst privat veröffentlichen.
7. Vor der Produktion auf einem Staging-Server testen.

## Headless-CLI

```bash
dotnet run --project src/PZAdvancedServerManager.Cli -- scan
dotnet run --project src/PZAdvancedServerManager.Cli -- steamcmd install
dotnet run --project src/PZAdvancedServerManager.Cli -- project create --name "Hauptserver"
dotnet run --project src/PZAdvancedServerManager.Cli -- project import-workshop --id <guid> --workshop-id 1234567890
dotnet run --project src/PZAdvancedServerManager.Cli -- project validate --id <guid>
dotnet run --project src/PZAdvancedServerManager.Cli -- project build --id <guid>
dotnet run --project src/PZAdvancedServerManager.Cli -- project publish --id <guid> --yes
dotnet run --project src/PZAdvancedServerManager.Cli -- automation run --interval 30
```

Jedes Projekt ist ein unabhängiges globales Paket. Ohne ausdrückliche Aktivierung durch den Administrator erfolgt keine automatische Aktualisierung. Beispielhafte systemd-Units liegen unter `deploy/systemd/`.

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
