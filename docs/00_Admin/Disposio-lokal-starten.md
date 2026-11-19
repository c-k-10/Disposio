# Disposio auf dem Desktop weiterentwickeln

Stand: 13.10.2026. Arbeitsbranch: `recovery/desktop-2026-10-13`.

## In Visual Studio öffnen

Öffne `src/Disposio/Disposio.slnx` in Visual Studio 2026 Community und wähle die Plattform `x64`.
Das normale Startprofil `Disposio (Package)` beginnt bei der Anmeldung. Es steht in der Liste an
erster Stelle. Zeigt Visual Studio neben dem grünen Start-Knopf ein anderes Profil, dort
`Disposio (Package)` auswählen. Das Profil
`Disposio (Dashboard)` ist ausschließlich im Debug-Build eine gekennzeichnete
Entwicklungsansicht. Sie verwendet echte Datenbankfunktionen; Speichern verändert echte Daten.
Das Profil `Disposio (Firmenansicht)` zeigt ebenfalls nur im Debug-Build die Firmenansicht mit
allen Büro-Rollen ohne Anmeldung. Sie lädt keine Firmendaten (siehe `docs/06_Design/Firmenansicht-WinUI.md`).

## Lokale Datenbankkonfiguration

Die früher öffentlich gespeicherten Neon-Zugangsdaten müssen in Neon widerrufen bzw.
rotiert werden. Alte Werte nicht erneut verwenden. Quellcodeänderungen widerrufen keine
Zugangsdaten, und die bisherigen Werte befinden sich weiterhin in der Git-Historie.

Trage die neue Verbindung außerhalb des Repositorys als Windows-Benutzervariable
`DISPOSIO_CONNECTION_STRING` ein: Windows-Suche nach „Umgebungsvariablen für dieses Konto bearbeiten“,
dann unter Benutzervariablen einen neuen Eintrag erstellen. Als Wert dient die neue
Npgsql-Verbindungszeichenfolge mit `Host`, `Database`, `Username`, `Password` und gegebenenfalls
`Port`. Verwende den einzelnen Neon-DNS-Host; der Port gehört in `Port`, nicht in `Host`.
Für direkte PostgreSQL-Verbindungen TLS mit Zertifikatsprüfung verwenden (`SSL Mode=VerifyFull`).
Sonderzeichen und zitierte Werte verarbeitet der Npgsql-Parser. Keine Zugangsdaten in
Startargumente, Git-Dateien, Screenshots, Chat oder Build-Protokolle schreiben.

Windows-Benutzervariablen sind keine verschlüsselte Geheimnisablage. Sie halten die
Konfiguration außerhalb von Git, sind aber für Prozesse unter deinem Benutzer lesbar.
Verwende eingeschränkte Datenbankrechte und keine produktiven Owner-Zugangsdaten für eine
verteilte Desktopanwendung. Eine spätere Backend-Lösung ist eine separate Architekturaufgabe.

Visual Studio nach dem Setzen neu starten, damit es die Variable an die Anwendung vererbt.
Ohne die Variable öffnet die Anwendung ihre Oberfläche, Datenbankoperationen schlagen jedoch
mit einer sicheren Fehlermeldung fehl. Es gibt keine eingebauten Ersatz-Zugangsdaten.

## Winston (KI-Co-Disponent) einrichten

Winston beantwortet in der Firmenansicht (Knopf „Winston“ oben rechts, nur für Disponenten) Fragen mit den
echten Firmendaten und arbeitet Verspätungsvorschläge in der Kundenkommunikation (D10) aus. Dafür ruft die
App Claude über die Anthropic-API auf (Modell `claude-haiku-5-5`, schnell und günstig; einstellbar in
`Services/WinstonKi.cs`). Die Verkehrslage (Staus, Baustellen,
Sperrungen auf der Reststrecke) kommt von TomTom, mit demselben Schlüssel wie die Live-Karte (D08).

Der API-Schlüssel kommt aus der [Claude Console](https://platform.claude.com) (Konto mit Guthaben) und wird
wie die Datenbankverbindung als Windows-Benutzervariable hinterlegt, nie im Code oder im Repository:

```powershell
setx ANTHROPIC_API_KEY "DEIN-SCHLÜSSEL"
```

Danach Visual Studio bzw. die App neu starten. Ohne Schlüssel läuft alles weiter: Winston zeigt einen
Hinweis, und Verspätungsvorschläge entstehen aus der festen Textvorlage.

- **Was Winston selbst tut:** alle 2 Minuten nach Verspätungen suchen (ETA mehr als 15 Minuten nach
  Fensterende), neue Vorschläge mit Begründung, Kunden- und Fahrertext ausarbeiten und melden.
  Gesendet wird nur nach „Freigeben & senden“ in D10.
- **Kosten (Haiku 5.5):** grob 0,2–0,5 Cent pro Frage an Winston und etwa 0,1 Cent pro ausgearbeitetem
  Vorschlag. Je Durchlauf arbeitet die KI höchstens 5 neue Vorschläge aus; „Mit Winston neu formulieren“
  kostet je Klick einen Aufruf. Mit Opus 5.5 wäre es etwa 40-mal so viel (5–10 Cent pro Frage).
- **Datenschutz:** Die Daten, die Winston für eine Antwort abfragt (Touren, Kunden, Fahrernamen,
  Nachrichten), gehen an Anthropic. Im fertigen Produkt gehört der Aufruf in ein Backend, damit der
  Schlüssel nicht auf jedem PC liegt.

## Datenbank und Anmeldung

Die Anwendung migriert das Datenbankschema nicht automatisch. Prüfe zunächst, welche Migrationen
bereits ausgeführt wurden. `Services/001_unternehmen_dashboard.sql` ergänzt das Unternehmensschema;
`Services/002_plattform_admin.sql` ergänzt Plattformkonten ohne Unternehmenszuordnung;
`Services/004_fahrzeug_aktiv.sql` ergänzt „Fahrzeug inaktiv setzen“ im Fuhrpark;
`Services/010_fahrerzuordnung_wartung.sql` ergänzt Fahrerzuordnung (F03) und Wartung (F04/F05).
Führe die Skripte nur nach Prüfung und Sicherung der Zieldatenbank aus. Die Wiederherstellung
hat weder Migrationen ausgeführt noch Konten angelegt.

Das vorhandene Werkzeug `tools/AdminKontoEinrichten` kann ein neues Plattformkonto anlegen.
Es verwendet dieselbe Umgebungsvariable und fragt E-Mail, Vorname und Nachname ab, danach das
neue Passwort lokal verdeckt. Das Passwort tippt die Person selbst ein, der das Konto gehört.
Bestehende Konten werden nicht überschrieben. Nur nach bewusstem Entschluss ausführen:

```powershell
dotnet run --project tools/AdminKontoEinrichten
```

Jeder Projektbesitzer hat ein eigenes MegakrassenAdmin-Konto (Stand 20.10.2026:
`admin@disposio.com` = Niklas Bäricke; Konten für Christina Kiesl und Pavlos Laskaridis
werden mit dem Werkzeug angelegt). Kein Sammelkonto teilen, sonst ist im Protokoll nicht
erkennbar, wer etwas geändert hat.

## Aktionsprotokoll

Anlegen und Bearbeiten eines Unternehmens im Dashboard schreibt im selben SQL-Befehl einen
Eintrag in `public.aktionsprotokoll`: Konto (`benutzer_id`), Zeitpunkt, Aktion und beim
Bearbeiten die geänderten Felder. Eine Anzeige in der App gibt es noch nicht. Nachsehen in
Neon (SQL Editor):

```sql
SELECT p.zeitpunkt, b.vorname || ' ' || b.nachname AS admin, p.aktion, u.firmenname, p.detail
FROM public.aktionsprotokoll p
JOIN public.unternehmen u ON u.unternehmen_id = p.unternehmen_id
LEFT JOIN public.benutzer b ON b.benutzer_id = p.benutzer_id
ORDER BY p.zeitpunkt DESC;
```

Einträge aus der Entwicklungsansicht (Debug-Profil ohne Anmeldung) haben kein Konto und
`akteur_art = 'Entwicklungsansicht'`.

Firmenkonten werden anhand von Kennung oder Firmenname und BCrypt-Passwort geprüft.
Eine eigene Firmenansicht ist im wiederhergestellten Branch noch nicht angebunden.
Das unternehmensübergreifende Dashboard benötigt ein Plattformkonto ohne Firma mit der
vorhandenen Rolle `SuperKrasserAdmin`. Die Oberfläche bezeichnet diese Rolle als MegakrassenAdmin.

## Build und Zusammenarbeit

Ein lokaler Debug-Build ist möglich mit:

```powershell
dotnet build src/Disposio/Disposio/Disposio.csproj -p:Platform=x64
```

`bin/` und `obj/` werden in allen Projekten ignoriert und sind nicht mehr versioniert.
Die Bereinigung ist ein eigener Commit (`3d60c59`). Die Funktionswiederherstellung ist
separat (`8f336eb`), basierend auf `origin/Dashboard_Daten_an_Datenbank` bei `da24915`.
Die Firmenname-Korrektur aus `origin/Login-Anmeldung-irgendwie-so` bei `65acec1` ist in
namensgleicher Funktion bereits enthalten. Dessen ungefilterter technischer Fehlerdialog
wurde zugunsten der sicheren Fehlermeldungen des Dashboard-Branches nicht übernommen.
`origin/master` wurde wegen fehlender gemeinsamer Historie nicht integriert.

Andere Entwickler erhalten die Änderungen erst, nachdem der Recovery-Branch veröffentlicht
und nach gemeinsamer Prüfung in ihren Zielbranch übernommen wurde. Beim ersten Pull können
lokal geänderte, bisher versionierte Build-Dateien den Vorgang blockieren: zuerst lokale Arbeit
sichern und den Zustand einzeln prüfen. Keine pauschale Übernahme von `ours`/`theirs` und kein
`reset --hard`. Ein neuer Build erzeugt benötigte Build-Artefakte wieder.

## Grenzen der Wiederherstellung

Der Ausgangsstand war `2d2766f` plus die separate Build-Bereinigung. Keine Stashes, offenen
Merge-Konflikte oder weiteren älteren lokalen Entwicklungs-Commits wurden gefunden.
Nie gepushte Änderungen auf dem anderen Rechner können aus diesem Repository nicht
rekonstruiert werden. Der Recovery-Branch enthält die geprüften zusätzlichen Quelldateien
beider relevanten Feature-Branches, nicht deren generierte Build-Artefakte.

Debug-x64-Build, Release-x64-Build (mit PublishReadyToRun=false) und Admin-Werkzeug wurden erfolgreich kompiliert. Es wurde kein Release-Paket veröffentlicht. Konfiguration,
Passwort-Sonderzeichen, Hostvalidierung, Rollenprüfung und Sitzungsende wurden mit
synthetischen Testdaten ohne Netzwerkzugriff geprüft. Diese Prüfungen ersetzen keine
interaktiven WinUI-, Login-, CRUD- oder Datenbanktests. Auf diesem PC wurden keine neuen
Datenbank-Zugangsdaten gefunden; echte Funktionsprüfungen stehen deshalb noch aus.

Der direkte Start der erzeugten EXE wurde versucht und scheiterte vor der Oberfläche:
`0x80040154` (Klasse nicht registriert) im Windows-App-SDK-Deployment-Initializer.
.NET 8 und Windows-App-Runtime-Pakete sind installiert; der genaue Registrierungs-/Paketkontext
muss noch geprüft werden. Starte zunächst über das paketierte Visual-Studio-Profil, damit
Visual Studio das MSIX-Deployment übernimmt. Dieser Startweg wurde noch nicht interaktiv getestet.
Die App wurde nicht als erfolgreich laufend bestätigt.

Lokale Sicherung vor der Recovery: `%LOCALAPPDATA%\Disposio-Git-Backups\recovery-20261013-163318`.
Dort liegen ein Git-Bundle mit allen damaligen Referenzen, der ursprüngliche Index sowie die
vollständig verschobenen alten Build-Ordner. Alte Builds und Git-Historie können die kompromittierten
Zugangsdaten enthalten; die Sicherung bleibt lokal und gehört nicht in ein öffentliches Repository.