# Disposio.web

Öffentliche Webseite von Disposio – reines HTML, CSS und JavaScript, ohne Build-Schritt.

```
Disposio.web/
├── index.html        Startseite mit App-Download
├── anmelden.html     Formular „Sendung anmelden“ für Kunden der Speditionen
├── css/style.css     Farben wie in der Desktop-App (Themes/FirmenStil.xaml)
├── js/config.js      Download-Link und Adresse der Neon Function
├── js/anmelden.js    Prüfung und Versand des Formulars
└── assets/logo.png
```

## Lokal ansehen

`index.html` per Doppelklick im Browser öffnen – oder in VS Code mit der Erweiterung „Live Server“.

Formular mit Speditions-Link testen: `anmelden.html?firma=demo-spedition`

## So kommt eine Sendung in die App

```
anmelden.html  →  neon-function/functions/sendung.ts  →  Tabellen kunde + auftrag  →  D02 in der App
```

- Beim Öffnen lädt das Formular die Speditionen (GET auf die Funktion) und zeigt sie im Auswahlfeld.
  Gesperrte Firmen (`status = 'Inaktiv'`) erscheinen nicht.
- Ein Link mit `?firma=<anmelde_code>` wählt die Spedition schon vor (Codes: `SELECT firmenname, anmelde_code FROM public.unternehmen;`).
- Web-Sendungen bekommen den Status **Angefragt** und erscheinen in D02 am **Abholtag**.
- Gibt es in der Firma schon einen Kunden mit derselben E-Mail, wird er verwendet, sonst wird ein neuer Kunde angelegt
  (Adresse = Abholadresse, kann in der App korrigiert werden).

## Einrichten

1. `009_web_anmeldung.sql` einmal im Neon SQL Editor ausführen (ist schon erledigt).
2. Funktion veröffentlichen, im Ordner `neon-function`:
   `neon functions deploy sendung --src functions/sendung.ts`
3. Adresse prüfen mit `neon functions get sendung` und in `js/config.js` bei `sendungApiUrl` eintragen.

Solange `sendungApiUrl` leer ist, läuft das Formular im **Demo-Modus**: Es wird nichts gesendet,
die Daten erscheinen in der Browser-Konsole (F12).

In diesen Ordner gehören **keine Geheimnisse** (Passwörter, Datenbank-Zugangsdaten) – jeder Besucher kann alles lesen.
