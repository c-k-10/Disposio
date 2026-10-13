# Dashboard in WinUI 3

Die Kundenübersicht liegt in `src/Disposio/Disposio/Views/DashboardAnsicht/`.
Sie setzt die beigefügten Dashboard-Mockups als native WinUI-Seite um.

## Bedienung

- In Visual Studio oben neben dem Startpfeil „Disposio (Dashboard)“ auswählen und mit F5 starten.
  Dieses separate Startprofil öffnet die Kundenübersicht direkt, ohne Anmeldedaten.
- „Disposio (Package)“ und „Disposio (Unpackaged)“ starten weiterhin mit der Login-Seite.
- Der vorhandene Demo-Login öffnet nach ausgefüllten Pflichtfeldern das Dashboard.
- Die Übersicht zeigt vier Beispielkunden, einen Kunden in Testphase und insgesamt 83 Benutzer.
- „Kunde anlegen“ öffnet das Formular mit Firmenname, Unternehmenskennung, E-Mail und Status.
- „Bearbeiten“ öffnet dasselbe Formular mit den aktuellen Kundendaten.
- „Speichern“ prüft Pflichtfelder, E-Mail-Syntax und eindeutige Unternehmenskennungen (unabhängig von Groß-/Kleinschreibung).
- „Abbrechen“ und Escape verwerfen Formulareingaben. Die Kennzahlen aktualisieren sich nach dem Speichern.
- Neue Kunden starten mit null Benutzern. Bearbeitung erhält die vorhandene Benutzerzahl.
- „Abmelden“ kehrt zum Login zurück; Farbe und Hell-/Dunkelmodus bleiben erhalten.

## Darstellung

| Akzentfarbe | Hexwert |
| --- | --- |
| Pink-Rot | `#FF004D` |
| Blau | `#007BFF` |
| Grün | `#00C853` |
| Orange | `#FF9100` |
| Lila | `#7B2CBF` |

Die Akzentfarbe betrifft Logo, Hauptaktionen, den Hellmodus-Schalter, Formularfokus und Testphase-Rahmen.
Die Schriftfarbe auf Akzentflächen wird anhand des Kontrasts gewählt.
Hell- und Dunkelmodus ändern Hintergründe, Eingabefelder, Rahmen und Textfarben einschließlich Dialogen.
In kleinen Fenstern wird die Seitenleiste ausgeblendet; Abmelden bleibt im Inhalt erreichbar.
Die Kundentabelle kann horizontal gescrollt werden. In schmalen Fenstern stehen die Kennzahlen untereinander.

## Stand der Anbindung

Die Kundendaten werden ausschließlich im Speicher gehalten und beim erneuten Öffnen des Dashboards zurückgesetzt.
Es gibt noch keine Backend-Authentifizierung, Datenbank oder Kundenansicht.
„Einloggen“ zeigt deshalb einen Hinweis, dass die Kundenansicht noch nicht angebunden ist.
Die vorhandene Demo-Anmeldung ist keine Prüfung von Zugangsdaten.

## Prüfung

Der Debug-Build für x64 wurde mit Visual Studio MSBuild erfolgreich erstellt.
Es wurden keine Projektabhängigkeiten ergänzt oder geändert.

Das separate Dashboard-Startprofil übergibt `--dashboard`. Die Anwendung liest den Parameter
über `Environment.GetCommandLineArgs()` und öffnet die Dashboard-Seite als erste Seite.

Die laufende Dashboard-Kopie wurde visuell im hellen und dunklen Modus geprüft.
Der Farbwechsel, das Anlegen eines Kunden, die Pflichtfeldprüfung und die Prüfung einer bereits
verwendeten Kennung wurden im Fenster ausgeführt. Ein neuer Testkunde erhöhte die Kennzahlen
von 4/1/83 auf 5/2/83 (Kunden/Testphase/Benutzer). Die Bearbeitung des Status eines vorhandenen
Kunden von Testphase auf Aktiv verringerte die Testphasenanzahl wieder auf 1; Benutzerzahlen blieben erhalten.
Der Dialog verwendet eigene Footer-Buttons, damit WinUI den gewählten Akzent auch bei Tastaturfokus beibehält.

Manuelle Prüffälle für die weitere Backend-Anbindung:

1. Jede der fünf Akzentfarben im hellen und dunklen Modus auswählen.
2. Kundenformular leer speichern; die Meldung und den Feldfokus prüfen.
3. Bestehende Kennung mit anderer Groß-/Kleinschreibung verwenden; Speichern wird verhindert.
4. Neuen Testkunden speichern; Kundenanzahl und Testphasenanzahl steigen um eins.
5. Status dieses Kunden auf Aktiv ändern; Testphasenanzahl sinkt wieder.
6. Bearbeitung abbrechen; Daten bleiben unverändert.
7. Abmelden und die übernommenen Darstellungseinstellungen prüfen.
