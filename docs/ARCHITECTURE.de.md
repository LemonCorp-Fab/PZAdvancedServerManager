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

Die Veröffentlichung arbeitet auf zwei Ebenen inkrementell. PZASM berechnet getrennte Fingerabdrücke für ausgelieferten Inhalt, Metadaten und Vorschaubild und lässt unveränderte Bereiche im VDF weg. SteamCMD und Steam vergleichen anschließend das eingereichte Manifest mit dem vorherigen und übertragen nur fehlende Chunks. PZASM lädt das Paket nach dem Upload niemals erneut herunter.

„Keine Änderung“ setzt alle drei lokalen Fingerabdrücke und eine neue öffentliche API-Abfrage der Remote-Handles für Inhalt und Vorschau, Dateigröße, Aktualisierungszeit, Titel, Beschreibung und Sichtbarkeit voraus. Fehlt ein Nachweis oder ist er veraltet, wird konservativ veröffentlicht. Der erzwungene Modus übergibt alle Bereiche an SteamCMD; Steam verwendet identische Remote-Chunks weiterhin wieder. Prozesscode `0` allein genügt nicht: Die aktuelle SteamCMD-Aktivität muss ausdrücklich `Upload finished ... : OK` bestätigen, und ein expliziter Workshop-Fehler hat Vorrang.

Der koordinierte Server bleibt während Build und Upload online. Wenn sich der ausgelieferte Inhalt geändert hat, wartet der Manager nach der Bestätigung die konfigurierte Frist — mindestens fünf Minuten —, sendet dann `save` und `quit` und führt die konfigurierte Neustartstrategie aus. Ein verifiziertes No-change sowie reine Metadaten- oder Vorschauänderungen starten den Server nicht neu.

Der Scheduler prüft Rechte und Abhängigkeiten, aktualisiert optional Quellen, baut, veröffentlicht und koordiniert den Server bei Bedarf über RCON. Eine überwachte Anmeldung übergibt das Passwort nur über die Standardeingabe an SteamCMD. Ein Konto ohne Steam Guard wird direkt angemeldet. Bei einem geschützten Konto sendet SteamCMD eine Bestätigungsanfrage an Steam Mobile und fragt deren Status automatisch ab, während die Oberfläche den aktiven Wartezustand zeigt. Der aktuelle Code wird nur angefordert, wenn die mobile Bestätigung abläuft oder der Benutzer die Ausweichmethode wählt; PZASM wiederholt die Anmeldung dann mit dem dokumentierten Befehl `set_steam_guard_code`, ebenfalls über die Standardeingabe. Steam bietet QR-Anmeldung im Client und im Web, SteamCMD stellt jedoch weder eine dokumentierte QR-Nutzlast noch einen QR-Anmeldebefehl bereit. Ein separater Web-QR kann diese Veröffentlichungssitzung daher nicht herstellen. SteamCMD behält sein eigenes Token im portablen Verzeichnis; manuelle und geplante Veröffentlichungen verwenden ausschließlich diese Sitzung. Der Manager speichert nur den Zeitpunkt der letzten erfolgreichen Prüfung. Eine abgelaufene Sitzung fordert zur erneuten Anmeldung auf, statt an einer unsichtbaren Eingabe zu warten. Die Oberfläche zeigt den Fortschritt live, erzwingt ein Zeitlimit und kann den externen Prozess abbrechen.

SteamCMD öffnet eine eigene Steam-Sitzung. Für die Automatisierung sollte deshalb ein dediziertes Veröffentlichungskonto mit Project Zomboid verwendet werden und nicht das im Desktop-Client aktive Konto. Die erste Anmeldung erzeugt das portable Token; spätere Prüfungen verwenden `steamcmd verify` ohne Passwort und ohne neues Token. PZASM importiert niemals Cookies oder Anmeldedateien des Steam-Clients. Eine Veröffentlichung über die Desktop-Sitzung würde eine autorisierte Steamworks-Anwendung erfordern: Der Herausgeber von Project Zomboid muss die AppID des Werkzeugs für `ISteamUGC` zu den Workshop App Publish Permissions hinzufügen; OAuth benötigt zusätzlich eine von Valve vergebene Client-ID mit AppID-begrenztem `write_cloud`-Zugriff. Ein externes Werkzeug kann sich diese Rechte nicht selbst erteilen.

## Externe Anwendung

Ein Spiel-Mod kann SteamCMD, Zeitpläne außerhalb des Spiels, private Dateien und mehrere Serverprofile nicht zuverlässig verwalten. PZASM besteht daher aus einer lokalen ASP.NET-Core-Anwendung und einer Headless-CLI mit gemeinsamem Kern. Nur der erzeugte Lua-Hinweis läuft in Project Zomboid.

SteamCMD ist eine verwaltete Abhängigkeit und keine manuelle Voraussetzung. Beim ersten Import, Quellen-Update, Veröffentlichen, Sitzungscheck oder bei der Wartung des Dedicated Servers verwendet PZASM eine gültige konfigurierte Datei oder lädt Valves Plattformarchiv nach `<data-root>/tools/steamcmd`, entpackt es kontrolliert, initialisiert es und verwendet danach denselben portablen Cache und dieselbe Sitzung weiter. Ein ungültig gewordener benutzerdefinierter Pfad fällt automatisch auf die verwaltete Kopie zurück. Öffentliche Project-Zomboid-Workshop-Inhalte werden standardmäßig anonym geladen. Die Oberfläche zeigt Download, Entpacken, Initialisierung und Prüfung live; ein Abbruch beendet auch den Initialisierungsprozess.

## Sicherheit und Rechte

Die [offizielle Richtlinie](https://projectzomboid.com/blog/modding-policy/) wird dem Administrator angezeigt; für seine Entscheidungen bleibt er allein verantwortlich. Berechtigungsstatus, Nachweise und Lesebestätigung dienen nur der Dokumentation und blockieren niemals Build, Veröffentlichung oder Automatisierung. Unbekannte, unbelegte oder abgelehnte Fälle bleiben deutlich als Warnungen sichtbar; private Nachweise bleiben außerhalb von `Contents`, und die öffentliche Beschreibung nennt alle Quellen.

Steam kann ein neues Item verbergen, bis die [Workshop-Vereinbarung](https://steamcommunity.com/workshop/workshopsubmitinfo/) akzeptiert wurde.

## Verbleibende Risiken

- Änderungen am Protokoll oder an Build 42;
- geänderte Mod-IDs, Abhängigkeiten, Karten oder Lizenzen;
- nicht deklarierte Abhängigkeiten und manuelle Kartenreihenfolge;
- statisch nicht erkennbare logische Konflikte;
- gelegentliche interaktive SteamCMD-Schritte;
- Serverneustart nur bei geändertem ausgeliefertem Inhalt, nach bestätigtem Upload und der konfigurierten Wartezeit.

## Lokale und entfernte Orchestrierung

Ein Profil beschreibt entweder eine lokale INI-Datei oder eine Verbindung zu einem entfernten VPS/Dedicated Host. Ein entferntes Profil kann ausschließlich RCON verwenden; SSH und INI-Verwaltung sind optional. Der Status führt eine echte RCON-Anmeldung aus, die Konsole kann unterstützte Verwaltungsbefehle senden und ein sauberes Beenden nutzt `save` und danach `quit`.

Lokale Profile besitzen einen expliziten Ausführungsmodus. Ein **lokales Host-Profil** wird über das Host-Menü des Spielclients gestartet und verwendet einen Prozess `zombie.network.GameServer -coop` sowie `coop-console.txt`. Ein **lokales Dedicated-Profil** wird über das separate Steam-Tool Project Zomboid Dedicated Server (AppID 380870) gestartet und verwendet `server-console.txt`. Beide Modi referenzieren absichtlich dieselben nativen Dateien `Zomboid/Server/<name>.ini`; der Manager speichert die gewählte Verwendung separat. Ein `-coop`-Hilfsprozess gilt nur bei gültigem aktuellem Startfortschritt oder einem Ready-Marker als aktiver Server; ein späterer Startfehler verhindert einen falschen Konflikt.

Mit systemd, Docker, einem Hosting-Panel oder einem anderen Supervisor, der Project Zomboid nach `quit` neu startet, kann ein reines RCON-Profil die Veröffentlichung koordinieren: zuerst wird der Workshop-Upload abgeschlossen, danach sendet der Manager `save` und `quit`. SSH dient nur der optionalen INI-Verwaltung oder einem expliziten Spiel-Startbefehl. Host-Befehle wie `reboot`, `shutdown` und `poweroff` werden abgelehnt. Das RCON-Geheimnis wird für unbeaufsichtigte Abläufe lokal gespeichert; dieses Verzeichnis muss geschützt werden.

## Kompatibilitäts- und Konfliktwerkstatt

Paketeditor und Server-Bereitstellungsansicht verwenden denselben zwischengespeicherten statischen Analysator. Er liest effektive Build-42-Strukturen (`common` plus den besten kompatiblen Versionsordner), `require`, `loadAfter`, `loadBefore`, `incompatible`, doppelte Mod-IDs, virtuelle Lua-/Skript-/Asset-Pfade, Kartenabhängigkeiten und überlappende `.lotheader`-Zellen. Unterschiedliche Dateien werden erst nach einem gemeinsamen Pfad- und Größenvergleich gehasht; identischer Inhalt wird als gelöste Information protokolliert.

Die Werkstatt schlägt eine stabile topologische Mod- und Kartenreihenfolge vor, zeigt genaue Belege und erlaubt es, einen priorisierten Gewinner zu wählen, eine beabsichtigte Kollision zu bestätigen oder eine Quelle zu deaktivieren. Manuelle Prioritäten werden zu expliziten Reihenfolgebedingungen und verändern niemals Dateien Dritter. Die Serverprüfung vergleicht das Paket zusätzlich mit `WorkshopItems`, `Mods`, `Map` und aktuellen Laufzeitfehlern. Eine statische Analyse kann beliebigen Lua-Code nicht als kompatibel beweisen; Tests im Spiel bleiben erforderlich.

Verletzungen der Reihenfolge durch harte Abhängigkeiten sind blockierend. Stark zusammenhängende Komponenten isolieren nur die Mods des tatsächlichen Zyklus, ohne alle nachgelagerten Mods einzubeziehen. Entsteht ein Zyklus ausschließlich durch einen manuell gewählten Kollisionsgewinner, der `require`, `loadAfter` oder `loadBefore` widerspricht, kann die Werkstatt ihn mit einem Klick reparieren: Sie entfernt nur die nachweislich ungültige manuelle Bedingung, baut den Graphen neu auf, validiert ihn und wendet anschließend die stabile topologische Reihenfolge an. Schlägt die Validierung weiterhin fehl, werden die entfernten Bedingungen wiederhergestellt. Zyklen, die ausschließlich aus von den Quellen deklarierten Bedingungen bestehen, bleiben manuell zu lösende Blockierungen.

Dateikollisionen werden zusätzlich nach Laufzeitauswirkung klassifiziert: Übersetzungen und passive Medien mit niedrigem Risiko, Client-Oberflächen mit mittlerem Risiko, gemeinsames Gameplay oder Skripte mit hohem Risiko sowie Server-Lua oder Kartendaten mit kritischem Risiko. Die Diagnose trennt diese Typen, zeigt den ersten kollidierenden virtuellen Pfad in jeder Kopfzeile und kann jede physische Quellkopie öffnen, nachdem ihr Speicherort innerhalb eines verwalteten Mod-Snapshots geprüft wurde.

Unterstützte Textkollisionen bieten einen schreibgeschützten Diff-Editor. Der Administrator kann zwei Quell-Mods wählen, die Seiten tauschen, Leerraum ignorieren, zwischen Neben- und vereinheitlichter Ansicht wechseln, suchen, nur Änderungen mit Kontext anzeigen und zwischen Änderungsblöcken navigieren. Die Hervorhebung innerhalb der Zeile zeigt die exakt geänderten Zeichen. Pfade werden vor dem Lesen erneut geprüft, Binärinhalte werden abgelehnt, Dateien sind auf 2 MiB und die Darstellung auf 12.000 Zeilen pro Seite begrenzt.

Die Kompatibilität besitzt einen eigenen Projekt-Tab. Das Dashboard zeigt nur eine kompakte Zustandsübersicht und öffnet diesen Tab ohne erneute Analyse. Stapelrezepte sind bewusst eng begrenzt: Sie können Mods mit nachweislich fehlender Zielversionsstruktur deaktivieren, Einträge ohne verfügbare Quelle oder wirksame `mod.info` deaktivieren und die berechnete Mod-/Kartenreihenfolge anwenden. Jeder Stapel zeigt seine genauen Ziele, behält Snapshots bei und überlässt mehrdeutige Dateikollisionen der ausdrücklichen Prüfung.

## Abhängigkeitsbewusste Importe

Jeder lokale oder Workshop-Import wird geprüft, bevor das Projekt geändert wird. Der Manager normalisiert die aus `mod.info` gelesenen `require=`-Mod-IDs, vergleicht sie mit dem aktuellen Paket und listet fehlende Abhängigkeiten im Bestätigungsdialog der Anwendung auf. Der Administrator kann den ausgewählten Mod mit allen auflösbaren Abhängigkeiten oder bewusst nur den ausgewählten Mod hinzufügen.

Lokale Abhängigkeiten werden über die exakte Mod-ID zugeordnet. Bei Workshop-Quellen liest PZASM zusätzlich die offizielle **Required Items**-Liste des Elements; Empfehlungen gelten niemals als Abhängigkeiten. Eine Ein-Klick-Reparatur ist sowohl im Diagnoseeintrag als auch auf der betroffenen Mod-Karte verfügbar. Ein heruntergeladenes Workshop-Kindelement wird nur akzeptiert, wenn sein effektives `mod.info` die angeforderte Mod-ID tatsächlich bereitstellt. Ohne verifizierte Quelle meldet der Manager die nicht aufgelöste ID, statt zu raten. Hinzugefügte Abhängigkeiten werden vor dem anfordernden Mod platziert; anschließend wird die gesamte Reihenfolge erneut validiert.

## Workshop-Entdeckungsfilter

Der öffentliche Workshop-Browser kombiniert die Sortierung der Steam Community mit deterministischer Filterung der öffentlichen Detailantwort. Die Suche kann Titel und Beschreibung gemeinsam oder einzeln prüfen. Mehrere erforderliche und ausgeschlossene Tags werden unterstützt; erforderliche Tags können vollständig oder teilweise übereinstimmen. Weitere Filter betreffen Veröffentlichungs-/Aktualisierungsalter, Ersteller-SteamID64, aktuelle und gesamte Abonnements, Favoriten, Aufrufe, minimale/maximale Dateigröße, Bild-/Beschreibungsverfügbarkeit und den bereits hinzugefügten Status im Ziel.

Die Suchtiefe ist explizit: Pro Manager-Stapel werden eine, drei oder fünf Steam-Ergebnisseiten geprüft. Kandidaten-IDs werden vor der gebündelten Detailabfrage dedupliziert und Browse-Ergebnisse kurz zwischengespeichert. Numerische und Metadatenfilter werden nach der Steam-Suche angewendet, damit ihr Verhalten deterministisch bleibt, selbst wenn die öffentliche Seite einen optionalen URL-Parameter ignoriert.
