# Architektur- und Machbarkeitsstudie

[English](ARCHITECTURE.md) · [Français](ARCHITECTURE.fr.md) · [Español](ARCHITECTURE.es.md) · [Deutsch](ARCHITECTURE.de.md) · [Português (Brasil)](ARCHITECTURE.pt-BR.md) · [简体中文](ARCHITECTURE.zh-CN.md)

## Ergebnis

Project Zomboid kann mehrere logische Mods aus einem Workshop-Item laden:

```text
eine Workshop-PublishedFileId
└── mods/
    ├── ModA/          → mod.info: id=ModA
    ├── ModB/          → mod.info: id=ModB
    └── PZASM_Notice/  → mod.info: id=PZASM_Notice_SUFFIX
```

Das Spiel sieht mehrere **Mod-IDs**, aber nur **eine zu synchronisierende Workshop-ID**. Damit wird der Versionskonflikt unabhängiger Quell-Items vermieden, ohne alle Dateien physisch zusammenzuführen.

## Client-/Server-Prüfung

Die untersuchte lokale Version 42.20.2 verarbeitet zuerst Workshop-IDs mit Zeitstempeln und lädt anschließend `Mods=` anhand der Mod-IDs. Logische Mods innerhalb desselben Items erhalten keinen eigenen Workshop-Zeitstempel.

Normale Integritätsprüfungen einschließlich `DoLuaChecksum` bleiben aktiv. Nach großen Spielupdates muss dieses Verhalten erneut getestet werden.

## Struktur und Konflikte

```text
steamapps/workshop/content/108600/<WorkshopId>/
└── mods/<LogischerOrdner>/
    ├── mod.info
    ├── media/
    ├── common/mod.info + media/
    └── 42.x/mod.info + media/
```

`media` kann Lua, Skripte, Karten, Texturen, Modelle, Animationen, Sounds, Radios, Übersetzungen und UI enthalten. Zwei Mods können dieselben Lua-Globals, Skript-IDs, Kartenzellen, Ressourcennamen oder Übersetzungsschlüssel verwenden. Eine reine Pfadumbenennung löst interne Referenzen nicht.

## Modi

**Bundle** ist empfohlen. Ursprüngliche Ordner und Mod-IDs bleiben unter einer Workshop-ID erhalten und bieten die höchste Kompatibilität.

**Strict Fusion** erzeugt `PZASM_Pack_<suffix>`, kombiniert effektive Inhalte, dedupliziert identische Dateien und stoppt bei jeder unterschiedlichen Kollision. Der Modus eignet sich nur für kontrollierte und getestete Sammlungen.

## Projekte und fixierte Versionen

Jedes Projekt besitzt eine unveränderliche GUID und eine eigene `publishedfileid`. `0` erstellt ein neues Item; SteamCMD schreibt die ID zurück und PZASM verwendet sie für spätere Aktualisierungen.

Beim Hinzufügen einer Quelle erstellt PZASM einen privaten Snapshot und berechnet dessen SHA-256. Builds verwenden diese fixierte Kopie statt des veränderlichen Steam-Caches. Eine explizite Aktualisierung ersetzt Snapshots atomar. `pack.lock.json` beschreibt exakt den ausgelieferten Inhalt.

## Veröffentlichung und Server

Der [Steamworks-Workshop-Leitfaden](https://partner.steamgames.com/doc/features/workshop/implementation) beschreibt Erstellung und Aktualisierung mit `workshop_build_item`.

Der Scheduler prüft Rechte und Abhängigkeiten, aktualisiert optional Quellen, baut, veröffentlicht und koordiniert den Server bei Bedarf über RCON. Eine überwachte Anmeldung übergibt das Passwort nur über die Standardeingabe an SteamCMD. Ein Konto ohne Steam Guard wird direkt angemeldet. Bei einem geschützten Konto sendet SteamCMD eine Bestätigungsanfrage an Steam Mobile und fragt deren Status automatisch ab, während die Oberfläche den aktiven Wartezustand zeigt. Der aktuelle Code wird nur angefordert, wenn die mobile Bestätigung abläuft oder der Benutzer die Ausweichmethode wählt; PZASM wiederholt die Anmeldung dann mit dem dokumentierten Befehl `set_steam_guard_code`, ebenfalls über die Standardeingabe. Steam bietet QR-Anmeldung im Client und im Web, SteamCMD stellt jedoch weder eine dokumentierte QR-Nutzlast noch einen QR-Anmeldebefehl bereit. Ein separater Web-QR kann diese Veröffentlichungssitzung daher nicht herstellen. SteamCMD behält sein eigenes Token im portablen Verzeichnis; manuelle und geplante Veröffentlichungen verwenden ausschließlich diese Sitzung. Der Manager speichert nur den Zeitpunkt der letzten erfolgreichen Prüfung. Eine abgelaufene Sitzung fordert zur erneuten Anmeldung auf, statt an einer unsichtbaren Eingabe zu warten. Die Oberfläche zeigt den Fortschritt live, erzwingt ein Zeitlimit und kann den externen Prozess abbrechen.

## Externe Anwendung

Ein Spiel-Mod kann SteamCMD, Zeitpläne außerhalb des Spiels, private Dateien und mehrere Serverprofile nicht zuverlässig verwalten. PZASM besteht daher aus einer lokalen ASP.NET-Core-Anwendung und einer Headless-CLI mit gemeinsamem Kern. Nur der erzeugte Lua-Hinweis läuft in Project Zomboid.

## Sicherheit und Rechte

Die [offizielle Richtlinie](https://projectzomboid.com/blog/modding-policy/) wird dem Administrator angezeigt; für seine Entscheidungen bleibt er allein verantwortlich. Berechtigungsstatus, Nachweise und Lesebestätigung dienen nur der Dokumentation und blockieren niemals Build, Veröffentlichung oder Automatisierung. Unbekannte, unbelegte oder abgelehnte Fälle bleiben deutlich als Warnungen sichtbar; private Nachweise bleiben außerhalb von `Contents`, und die öffentliche Beschreibung nennt alle Quellen.

Steam kann ein neues Item verbergen, bis die [Workshop-Vereinbarung](https://steamcommunity.com/workshop/workshopsubmitinfo/) akzeptiert wurde.

## Verbleibende Risiken

- Änderungen am Protokoll oder an Build 42;
- geänderte Mod-IDs, Abhängigkeiten, Karten oder Lizenzen;
- nicht deklarierte Abhängigkeiten und manuelle Kartenreihenfolge;
- statisch nicht erkennbare logische Konflikte;
- gelegentliche interaktive SteamCMD-Schritte;
- erforderlicher Serverneustart nach der Veröffentlichung.

## Lokale und entfernte Orchestrierung

Ein Profil beschreibt entweder eine lokale INI-Datei oder eine Verbindung zu einem entfernten VPS/Dedicated Host. Ein entferntes Profil kann ausschließlich RCON verwenden; SSH und INI-Verwaltung sind optional. Der Status führt eine echte RCON-Anmeldung aus, die Konsole kann unterstützte Verwaltungsbefehle senden und ein sauberes Beenden nutzt `save` und danach `quit`.

Mit systemd, Docker, einem Hosting-Panel oder einem anderen Supervisor, der Project Zomboid nach `quit` neu startet, kann ein reines RCON-Profil die Veröffentlichung koordinieren: zuerst wird der Workshop-Upload abgeschlossen, danach sendet der Manager `save` und `quit`. SSH dient nur der optionalen INI-Verwaltung oder einem expliziten Spiel-Startbefehl. Host-Befehle wie `reboot`, `shutdown` und `poweroff` werden abgelehnt. Das RCON-Geheimnis wird für unbeaufsichtigte Abläufe lokal gespeichert; dieses Verzeichnis muss geschützt werden.
