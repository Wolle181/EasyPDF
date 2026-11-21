# EasyPDF

*English: [README.md](README.md)*

Eine Desktop-App für Windows, macOS und Linux zum **Lesen und Neuordnen von PDF-Dokumenten** – schnell, einfach und
komplett offline. Jede Änderung passiert zuerst nur auf dem Bildschirm und lässt sich rückgängig machen; die Datei
ändert sich erst beim Speichern.

Die App ist auf Wunsch **White-Label**: Jedes Unternehmen kann sie auf den eigenen Namen und das eigene Branding (Icon,
Farben und Herausgeber) umstellen, etwa für den internen Einsatz (siehe [Anpassen](#anpassen)). Das Original ist EasyPDF;
unten steht `<App>` für den Namen der verwendeten Marke. Der Copyright- und Lizenzhinweis muss erhalten bleiben (MIT,
siehe [Lizenz](#lizenz)).

**Download:** Installer für Windows, Linux und macOS sowie eine portable Windows-Version (.zip, entpacken und starten) in der Vorabversion
[latest](../../releases/tag/latest) (gebaut von GitHub Actions,
[docs/BUILDING.md](docs/BUILDING.md#without-local-tools-github-actions), Englisch).
**Vorstellungsvideo:** Eine zweiminütige Vorstellung (Deutsch und Englisch) wird mit der App ausgeliefert: Der Video-Button
im Kopf spielt sie ab. Die Web-Version hat eine eigene Vorstellung (Hochladen, Teilen, Desktop-Download). Aufgenommen
werden beide pro Marke mit `branding/<id>/make-presentation-videos.cmd`.

## Funktionen

### Lesen
- Seiten nebeneinander mit horizontalem Scrollen – Mausrad, Pfeiltasten oder **Wischen mit der Maus** (ein schneller Wisch gleitet weiter)
- Anpassen an Höhe, Breite oder Seite, freier Zoom um den Mauszeiger (Strg/Cmd + Mausrad, Pinch)
- Seitenleiste mit Miniaturen, die der gelesenen Seite folgen; Sprung zu jeder Seite per Klick oder Strg/Cmd+G; am Rand breiter ziehen für größere Miniaturen
- **Text suchen** mit Strg/Cmd+F: alle Treffer werden markiert, Enter/F3 springt zum nächsten (durchsucht die Textebene und erkannten Text)
- Text markieren und kopieren; auch bei sehr großen Dateien schnell (nur sichtbare Seiten werden geladen)

### Seiten organisieren
- Miniaturenraster mit Mehrfachauswahl und **Drag & Drop** zum Umsortieren
- Drehen (schnell oder für alle/ausgewählte Seiten/einen Bereich wie `1-3, 5`), löschen, leere Seiten einfügen
- Seiten anderer PDFs vor oder nach einer Seite einfügen, Seiten durch Seiten eines anderen Dokuments ersetzen
- Mehrere Dateien zu einem neuen Dokument **zusammenführen**, Seiten **extrahieren**, ein Dokument **teilen**
- Seiten aus anderen Dateien tragen eine farbige Markierung, man sieht also immer, woher sie stammen

### Auf Seiten ergänzen
- **Stempel**: Unterschriften, Firmenstempel, „Bezahlt“, Nummernstempel 00–20 … aus einem Stempelordner; Stempel mit
  Feldern setzen beim Platzieren das Datum ein („Eingegangen 9. Nov. 2026“)
- **Text**: auf die Seite klicken und tippen; Farbe, Schrift und Größe werden gemerkt; gespeichert als echter,
  durchsuchbarer PDF-Text
- **Vorlagen**: Textbausteine mit Platzhaltern für Datum, Uhrzeit, Benutzer, Dateiname, Seitenzahl
- **Bereich kopieren**: einen Teil einer Seite einrahmen und als Bild auf einer anderen Seite oder in einem anderen
  Dokument einfügen

### Schützen und weitergeben
- **Schwärzen / Abdecken**: verdeckt vertrauliche Stellen; beim Speichern wird alles unter den Boxen endgültig aus der
  Datei entfernt (Text, Bilder, Zeichnungen, Formulareinträge)
- **Texterkennung (OCR)** für Scans, Deutsch und Englisch: legt eine unsichtbare Textebene an, sodass gescannte Seiten
  in jedem PDF-Betrachter durchsuchbar und kopierbar werden – offline, die Modelle werden mitgeliefert
- **Dateigröße verkleinern** (Scans und Bilder mit unnötig hoher Auflösung), **drucken**, **per E-Mail senden** –
  immer mit den aktuellen Änderungen, auch ungespeicherten
- **Elektronische Rechnungen (ZUGFeRD, Factur-X, XRechnung):** eine Leiste über den Seiten weist auf das eingebettete
  Rechnungs-XML hin, das sich mit einem Klick im Programm des Systems öffnen oder speichern lässt; **Anhänge…** listet
  alle in die PDF eingebetteten Dateien
- Rückgängig/Wiederholen für jeden Schritt; beim Speichern bleiben Lesezeichen, Metadaten, Formulare und Anhänge erhalten

### Bedienung
- Ein Fenster pro Dokument; Menüleiste (auf macOS systemeigen), Ribbon, Tastenkürzel, zuletzt geöffnete Dateien,
  Dateien per Drag & Drop öffnen
- Eingebaute Kurzanleitung (F1) und Vorstellungsvideo, helles und dunkles Design, Deutsch und Englisch
- Einbindung in den Dateimanager: *Mit <App> lesen* und *Mit <App> zusammenführen* im Rechtsklick-Menü (Windows
  Explorer, Dolphin, Nemo; *Öffnen mit* im Finder und in GNOME Dateien)

### Web-Version
Dieselbe Oberfläche läuft auch **im Browser** (WebAssembly), mit Drag & Drop von Seiten und von Dateien vom Desktop:
öffnen, lesen, Seiten ordnen, drehen, löschen und einfügen, Dateien zusammenführen, Seiten extrahieren, ein Dokument
teilen (ZIP) und das Ergebnis herunterladen. Die PDFs verarbeitet ein Server im Container (pdfium), dort liegen sie nur vorübergehend (30 Minuten nach
der letzten Nutzung, höchstens 8 Stunden). Ohne Anmeldung für den privaten Gebrauch oder mit Single Sign-on (OpenID
Connect: Keycloak, Entra ID, …) im Unternehmen; Bauen und Betrieb in [docs/BUILDING.md](docs/BUILDING.md#web-version-container).
Text lässt sich markieren, kopieren und suchen, Texte setzen, Bereiche schwärzen, abdecken und kopieren; Texterkennung (OCR) läuft auf dem
Server; Stempel (mit Datumsfeldern) und Textvorlagen bleiben im Browser und lassen sich als ZIP-Datei exportieren und
importieren, gemeinsame kommen vom Server; Drucken öffnet die PDF in einem neuen Tab; „Teilen“ (statt „E-Mail“) übergibt die PDF an das Teilen-Fenster des Systems:
E-Mail-Programm, Messenger, AirDrop … (Windows, macOS, Android, iOS; wo der Browser keine Dateien teilen kann, z. B.
unter Linux oder in Firefox, bietet es den Download an). Die XML einer E-Rechnung öffnet als Text in einem neuen Tab.
Die Desktop-Versionen (Windows-Installer und portabel, macOS, Linux .deb und .tar.gz) lassen sich aus der Web-Version
herunterladen: System wählen, das eigene ist vorausgewählt.

## Anpassen

### Eigene Marke (White-Label)
Alles, was die App kennzeichnet, kommt aus einem Markenprofil in `branding/<id>/` – ohne Änderungen am Code:

| Was | Wo es erscheint |
|---|---|
| **Name** | Fenstertitel, alle Texte, Programmdatei, Installer, Startmenü, Kontextmenü, Installations- und Einstellungsordner |
| **Zwei Markenfarben** (dunkle Primär-, helle Akzentfarbe) | das ganze Farbschema in hellem und dunklem Design wird daraus abgeleitet |
| **Icon oder Logo** | Programm-Icon (alle Größen), Markenkopf des Fensters; in den Markenfarben gezeichnet oder aus dem eigenen Logo erzeugt |
| **Firmenlogo** | Hilfe ▸ Über |
| **Herausgeber, Copyright, Autor** | Hilfe ▸ Über, Dateieigenschaften, Installer |
| **Demovideos** | mit Name, Farben und Icon der Marke aufgenommen |

Jede Marke lässt sich neben anderen installieren, mit eigenen Einstellungen. Gebaut wird immer eine Marke, ohne Angabe
`easypdf` (`<Brand>` in `Directory.Build.props`); eine andere mit `-p:Brand=<id>` (oder die Variable `EASYPDF_BRAND`,
oder `-Brand <id>` der Installer-Skripte). Marke anlegen und ändern:
[branding/README.md](branding/README.md) (Englisch); mit Claude Code führt der Skill `branding` Schritt für Schritt durch.

### Stempel und Vorlagen
- **Stempelordner** (Standard `Dokumente/<App>/Stamps`, beliebiger Ordner in den Einstellungen): Jedes PNG darin ist
  ein Stempel, Unterordner werden Abschnitte der Galerie. Bilder mit dem Dateimanager hineinkopieren – oder Stempel im
  eingebauten **Stempel-Editor** gestalten: ein Bild plus Textfelder mit Platzhaltern (z. B. das Datum eines
  „Eingegangen“-Stempels), gespeichert im PNG selbst, ein Stempel ist also eine Datei, die sich weitergeben lässt.
- **Vorlagenordner** (Standard `Dokumente/<App>/Templates`): eine Textdatei pro Vorlage, bearbeitbar in der App oder
  in jedem Editor. Platzhalter wie `{$DATE:yyyy-MM-dd}`, `{$USER}`, `{$FILENAME}`, `{$PAGE}`/`{$PAGES}` werden beim
  Platzieren ausgefüllt.
- Muster-Stempel und -Vorlagen kommen beim ersten Start mit; gelöschte Muster kehren nicht zurück.

### Einstellungen
Design (System, hell, dunkel), Sprache (System, Deutsch, Englisch), Stempel- und Vorlagenordner, Warnung vor dem
Überschreiben einer Datei mit geschwärzten Inhalten und „PDF-Dateien mit <App> öffnen“ (als Standard-PDF-Programm
festlegen).

## Kommandozeile

```
<App> [--read | --merge] [datei.pdf ...]          (Linux: <app>, der Name in Kleinbuchstaben)
```
Ein PDF öffnet sich zum Lesen; mehrere öffnen den Dialog *PDFs zusammenführen* in der angegebenen Reihenfolge. `--read`
öffnet jede Datei in einem eigenen Fenster, `--merge` geht immer in den Zusammenführen-Dialog (Dateien mehrerer
gleichzeitig gestarteter Prozesse landen in einem Dialog).

## Bauen
.NET 11 SDK, dann `dotnet build EasyPDF.slnx` und `dotnet test EasyPDF.slnx`. Voraussetzungen, Installer für Windows
(MSI, portable .zip), Linux (.deb/.tar.gz) und macOS (.dmg), Versionsnummern und GitHub Actions:
**[docs/BUILDING.md](docs/BUILDING.md)** (Englisch).

## Dokumentation
- [docs/BUILDING.md](docs/BUILDING.md) – Bauen, Installer, CI (Englisch)
- [branding/README.md](branding/README.md) – Markenprofile (Englisch)
- [docs/DESIGN.md](docs/DESIGN.md) – Entwurfsentscheidungen und UX-Beschreibung
- [CLAUDE.md](CLAUDE.md) – Architektur und Konventionen
- [docs/TEST_CHECKLIST.md](docs/TEST_CHECKLIST.md) – manuelle Testliste (vor allem macOS/Linux)

## Lizenz
MIT – siehe [LICENSE](LICENSE). Die mitgelieferten Open-Source-Komponenten und ihre Lizenzen stehen in
[licenses/THIRD-PARTY-NOTICES.txt](licenses/THIRD-PARTY-NOTICES.txt) (auch in der App: Hilfe ▸ Über ▸ Lizenzen Dritter).
