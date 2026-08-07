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

Der Scheduler prüft Rechte und Abhängigkeiten, aktualisiert optional Quellen, baut in einem temporären Verzeichnis, führt RCON-`save` und `quit` aus, veröffentlicht und startet einen zuvor laufenden Server neu. Steam-Passwörter und Steam-Guard-Codes werden nicht gespeichert.

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

Ein Profil beschreibt entweder eine lokale INI-Datei oder eine Verbindung zu einem entfernten VPS/Dedicated Host. Der Status ist keine einfache TCP-Port-Prüfung: PZASM authentifiziert sich über RCON und meldet Project Zomboid nur dann als aktiv, wenn das Passwort akzeptiert wird. Ein sauberes Beenden sendet immer `save` und danach `quit` per RCON.

SSH wird nur für Verbindungstests, die entfernte INI-Datei und den konfigurierten Startbefehl des Project-Zomboid-Prozesses oder -Dienstes verwendet. Der Zugriff erfolgt nicht interaktiv über privaten Schlüssel oder SSH-Agent. Host-Befehle wie `reboot`, `shutdown` und `poweroff` werden abgelehnt. Eine koordinierte Veröffentlichung stoppt und startet ausschließlich das Spiel; das Betriebssystem des Hosts läuft weiter. Das RCON-Geheimnis wird für unbeaufsichtigte Abläufe in den lokalen Manager-Profildaten gespeichert; dieses Verzeichnis muss geschützt werden.
