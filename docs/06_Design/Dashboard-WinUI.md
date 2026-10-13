# Dashboard: Bedienung und Datenbank verstehen

Die Kundenübersicht liegt in `src/Disposio/Disposio/Views/DashboardAnsicht/`.
Unternehmen werden in der vorhandenen Neon-Datenbank dauerhaft gespeichert. Die Anwendung
liest die Tabelle `public.unternehmen`; sie legt keine Beispieldaten beim Start an.

## Starten und bedienen

- Für die normale Anmeldung in Visual Studio **„Disposio (Package)“** auswählen und F5 drücken.
  „Disposio (Unpackaged)“ beginnt ebenfalls bei der Login-Seite.
- Im Login den Kontotyp wählen: **„Firmenkonto“** verlangt eine Unternehmenskennung;
  **„MegakrassenAdmin“** verlangt nur E-Mail und Passwort, weil die Projektbesitzer keiner
  Kundenfirma angehören. Ein gültiges Firmenkonto erhält keinen Zugang zur globalen Kundenverwaltung.
- Nach erfolgreicher Anmeldung als MegakrassenAdmin öffnet sich das Dashboard. Es zeigt den
  Namen und die Rolle des angemeldeten Kontos. Nach einem Neustart ist eine neue Anmeldung nötig.
- **„Disposio (Dashboard)“** übergibt `--dashboard` und öffnet ausschließlich im **Debug-Build**
  eine ausdrücklich als **„Entwicklungsansicht“** bezeichnete Vorschau ohne Benutzeranmeldung.
  Im **Release-Build** führt auch `--dashboard` zur Login-Seite. Die Vorschau ist kein Admin-Konto.
- Beim Öffnen werden die Unternehmen aus Neon geladen. „Erneut laden“ holt den aktuellen Stand,
  beispielsweise nach einer Änderung durch einen Kollegen.
- Die Kennzahlen zeigen Unternehmen, Unternehmen in Testphase und tatsächlich zugeordnete Benutzer.
  Vor dem ersten erfolgreichen Laden zeigt ein Strich, dass die Zahlen noch unbekannt sind.
  Eine leere Datenbank ergibt eine leere Liste und drei Nullen.
- „Kunde anlegen“ und „Bearbeiten“ öffnen das Formular. Pflichtfelder sind Firmenname,
  Unternehmenskennung, E-Mail des Kundenadmins, Adresse, PLZ, Ort, Telefon und Kontaktperson.
  Der Status ist „Testphase“ oder „Aktiv“. Die Kontakt-E-Mail ist optional; bleibt sie leer,
  wird die E-Mail des Kundenadmins verwendet.
- „Speichern“ prüft die Eingaben und wartet auf die Bestätigung aus Neon. Erst danach ändern
  sich die Liste und die Kennzahlen. Währenddessen ist ein zweites Speichern gesperrt.
- Bei einem Speicherfehler bleiben die Eingaben erhalten. Bei einem Verbindungsabbruch kann
  der Server schon gespeichert haben: Deshalb vor einem erneuten Versuch die Liste neu laden.
- „Abbrechen“ und Escape verwerfen den Formularentwurf. Während eines laufenden Speicherns
  bleibt der Dialog offen, bis das Ergebnis feststeht.
- „Abmelden“ löscht die Sitzung, die geladenen Kundendaten der Anzeige und den Navigationsverlauf.
  Danach erscheint wieder der Login. Akzentfarbe und Hell-/Dunkelmodus werden übernommen.

## Das neue Admin-Konto einrichten

Das vorgesehene neue Konto heißt **`admin@disposio.com`**. Es bekommt keine Firmenkennung und
keinen Eintrag in der Unternehmenstabelle. In `public.benutzer` ist seine `unternehmen_id`
deshalb `NULL`: Das bedeutet „dieses Konto gehört keiner Kundenfirma“.

Die Datenbank verwendet bereits die Rolle **`SuperKrasserAdmin`**. Dieselbe Rolle heißt in der
Oberfläche **„MegakrassenAdmin“**. Die Verbindung zwischen Konto und Rolle steht in
`public.benutzer_rolle`; die Rollenbezeichnung steht in `public.rolle`. Ein Konto ohne Firma
allein reicht nicht: Es muss aktiv sein und genau diese Rolle besitzen.

Das lokale Werkzeug liegt in **`tools/AdminKontoEinrichten`**. Aus dem Repository-Hauptordner
wird es in einer eigenen lokalen Konsole gestartet:

```powershell
dotnet run --project tools/AdminKontoEinrichten/AdminKontoEinrichten.csproj
```

Das Passwort wird dort zweimal direkt eingegeben und nur als Sternchen angezeigt. Es muss
mindestens **15 Zeichen** enthalten und darf höchstens **72 UTF-8-Bytes** belegen. Bei Umlauten
oder anderen Unicode-Zeichen sind Zeichenanzahl und Byteanzahl unterschiedlich. Die Obergrenze
verhindert, dass BCrypt einen Teil des Passworts unbemerkt abschneidet.

Das Werkzeug speichert einen BCrypt-Hash und die Rollenzuordnung gemeinsam. Es überschreibt
kein vorhandenes Konto. Passwörter gehören weder in den Chat noch in Startargumente, Quellcode
oder diese Dokumentation. Danach im normalen Disposio-Login **„MegakrassenAdmin“** wählen,
`admin@disposio.com` eingeben und das selbst gewählte Passwort verwenden.

## Welche Datei macht was?

| Datei | Aufgabe |
| --- | --- |
| `Views/DashboardAnsicht/DashboardAnsicht.xaml` | Beschreibt das Aussehen: Liste, Kennzahlen, Buttons und Farben. |
| `Views/DashboardAnsicht/DashboardAnsicht.xaml.cs` | Reagiert auf Klicks, prüft Formulare und aktualisiert die Anzeige. |
| `Views/LoginPage/LoginPage.xaml` und `.xaml.cs` | Zeigen die Kontoauswahl und Eingabefelder, rufen die Anmeldung auf und öffnen das Dashboard nur für einen geprüften Plattformadministrator. |
| `App.xaml.cs` und `MainWindow.xaml.cs` | Bestimmen die Startseite. Die direkte Vorschau wird nur im Debug-Build zugelassen. |
| `Services/AuthService.cs` | Sucht das Konto, prüft den Passwort-Hash und liest die Rollen. Prüft außerdem vor jedem Unternehmenszugriff die aktuelle Berechtigung erneut. |
| `Services/BenutzerSitzung.cs` | Merkt sich das angemeldete Konto nur während der laufenden Anwendung; speichert weder Passwort noch Hash. |
| `Models/Unternehmen.cs` | Bündelt die Unternehmensdaten. Ein Entwurf enthält Eingaben; ein gespeicherter Datensatz enthält zusätzlich Id, Benutzerzahl und Version. |
| `Services/UnternehmenRepository.cs` | Führt die SQL-Abfragen zum Laden, Anlegen und Bearbeiten aus. |
| `Services/NeonSqlClient.cs` | Überträgt SQL und getrennte Parameter über Neons HTTPS-Schnittstelle. |
| `Services/Database.cs` | Stellt die bereits vorhandene Verbindungseinstellung bereit. |
| `Services/001_unternehmen_dashboard.sql` | Enthält die einmalige Ergänzung der vorhandenen Tabelle. |
| `Services/002_plattform_admin.sql` | Erlaubt Plattformkonten ohne Firma und verhindert doppelte Plattform-E-Mails. Verwendet die vorhandene Admin-Rolle. |
| `tools/AdminKontoEinrichten` | Erstellt das neue Admin-Konto mit einer ausschließlich lokalen Passworteingabe. |

Der Ablauf bei der Anmeldung lässt sich so merken:

```text
Kontotyp, E-Mail und Passwort im Login eingeben
    ↓
AuthService sucht ein aktives, eindeutiges Konto und liest seine Rollen
    ↓
BCrypt prüft das Passwort gegen den gespeicherten Hash
    ↓
Keine Firma + Rolle SuperKrasserAdmin → geprüfter Plattformadministrator
    ↓
BenutzerSitzung merkt sich das Konto; die Oberfläche öffnet das Dashboard
    ↓
Vor jedem Laden oder Speichern: aktive Admin-Rolle erneut in Neon prüfen
    ↓
Rolle entfernt oder Konto deaktiviert → Zugriff ablehnen und zum Login zurückkehren
```

Die Kontoauswahl in der Oberfläche erteilt keine Rechte. Sie sagt dem Dienst nur, welche Art
von Konto gesucht werden soll. Auch eine direkte Navigation zur Dashboard-Seite reicht nicht:
Die Seite prüft die Sitzung und das Repository prüft die aktuelle Berechtigung in der Datenbank.

Der Ablauf beim Speichern lässt sich so merken:

```text
Formular ausfüllen
    ↓
Eingaben prüfen → bei Fehler: Meldung zeigen, Eingaben behalten
    ↓
UnternehmenEntwurf erstellen
    ↓
Repository sendet SQL + Parameter an Neon
    ↓
Neon speichert und liefert den gespeicherten Datensatz zurück
    ↓
Dashboard übernimmt den Datensatz und berechnet die Kennzahlen
```

Die `ObservableCollection<Kunde>` ist die Anzeige-Kopie im Arbeitsspeicher. Sie meldet der
XAML-Liste neue oder entfernte Zeilen. `PropertyChanged` meldet Änderungen einzelner Felder.
Die dauerhaften Daten stehen in Neon und bleiben nach dem Schließen des Fensters erhalten.

## Datenbank und Verbindung

Die Migration ergänzt `public.unternehmen` um die Dashboard-Felder `kennung`, `admin_email`,
`status` und `updated_at`. Die vorhandenen Kontaktfelder werden mitgespeichert. Die Einrichtung
ist ein eigener, einmaliger Schritt; die Anwendung verändert das Datenbankschema nicht beim Start.
Bestehende Unternehmen und ihre Beziehungen zu Benutzern bleiben erhalten.

`002_plattform_admin.sql` ergänzt diese Struktur für die Projektbesitzer: `unternehmen_id`
in `public.benutzer` darf jetzt `NULL` sein. Die Fremdschlüsselprüfung für Firmenkonten bleibt
bestehen. Ein zusätzlicher eindeutiger Index verhindert doppelte Plattformkonten mit derselben
E-Mail auch bei unterschiedlicher Groß-/Kleinschreibung oder äußeren Leerzeichen. Die Migration
legt noch kein Benutzerkonto und kein Passwort an; das übernimmt das lokale Einrichtungswerkzeug.

Die Benutzerzahl wird aus den über den Fremdschlüssel zugeordneten Datensätzen in `public.benutzer`
gezählt. Sie ist kein manuell gepflegter Beispielwert. Ein neues Unternehmen hat zunächst null
Benutzer, solange ihm keine Benutzer zugeordnet sind.

Neue Unternehmen erhalten die vorhandenen Pflichtwerte für die Passwortregel: Mindestlänge 8
und Beschreibung „Mindestens 8 Zeichen“. Beim Bearbeiten bleibt eine bestehende Passwortregel
erhalten. Das Formular erstellt keinen Admin-Benutzer und kein Passwort: Die Admin-E-Mail ist
hier eine gespeicherte Kontaktinformation.

Der Dashboard-Datenzugriff nutzt HTTPS auf Port 443. Dadurch funktioniert er auch in Netzen,
die den direkten PostgreSQL-Port 5432 sperren. Dafür werden `HttpClient` und die vorhandenen
.NET-Bibliotheken verwendet; es kommen keine neuen Paketabhängigkeiten hinzu. Die vorhandene
Npgsql-Verbindungsmethode bleibt für andere Projektteile nutzbar.

Optional kann `DISPOSIO_DB_CONNECTION_STRING` die vorhandene Verbindungseinstellung ersetzen.
Der Wert gehört in die lokale Umgebung, nicht in diese Dokumentation oder in Fehlermeldungen.
Ohne diese Variable wird die bereits vorhandene Einstellung aus `Database.cs` verwendet.

Diese Anwendung verbindet sich weiterhin direkt aus dem Desktop mit einer gemeinsamen
Datenbankkonfiguration. Es gibt noch keine eigene Server-API, die Benutzeranmeldung und Rechte
unabhängig vom Desktop durchsetzt. Die Rollenprüfungen schützen den vorgesehenen Ablauf in der
Anwendung; sie ersetzen keine serverseitige Zugriffskontrolle für eine verteilte Produktivversion.

## Fragen fürs Fachgespräch

**Was unterscheidet Anmeldung und Berechtigung?** Die Anmeldung prüft, ob das Passwort zum
Konto gehört. Die Berechtigungsprüfung entscheidet danach, welche Funktionen dieses Konto
verwenden darf. Ein korrektes Firmenpasswort öffnet deshalb nicht das globale Dashboard.

**Warum hat ein MegakrassenAdmin keine Firma?** Er verwaltet das Projekt und alle Kundenfirmen.
`NULL` in `unternehmen_id` beschreibt diese fehlende Firmenzuordnung. Die Rolle
`SuperKrasserAdmin` erteilt zusätzlich das Recht auf die globale Verwaltung.

**Warum wird die Rolle bei jedem Datenzugriff erneut geprüft?** Ein Konto kann nach der
Anmeldung deaktiviert oder seine Rolle entfernt werden. Eine alte Sitzung soll solche Änderungen
nicht dauerhaft umgehen. Deshalb liest `AuthService` die Berechtigung vor dem Laden und Speichern
erneut aus der Datenbank.

**Was ist ein Passwort-Hash?** BCrypt berechnet einen Prüfwert aus dem Passwort mit zufälligem
Salt. Beim Login vergleicht die Bibliothek das eingegebene Passwort mit diesem gespeicherten
Prüfwert. Das Passwort selbst steht weder in der Benutzerzeile noch in der Sitzung.

**Was speichert die Sitzung?** Die Benutzer-Id, den Namen, die Sprache, die Firmenzuordnung
und die Rollen. Sie liegt im Arbeitsspeicher und wird beim Abmelden oder Beenden verworfen.
Passwort und Hash werden nicht darin gespeichert.

**Warum funktioniert die Dashboard-Vorschau ohne Passwort?** Das ausdrückliche Debug-Profil
ist eine Entwicklungshilfe zum Gestalten der Oberfläche. Es zeigt „Entwicklungsansicht“ und
erzeugt kein angemeldetes Admin-Konto. Beim Release-Build wird dieser Einstieg ausgeschlossen.

**Was ist der Primärschlüssel?** Die Id ist die eindeutige Nummer eines Unternehmens.
Sie bleibt gleich, auch wenn Firmenname oder Kennung geändert werden. Beziehungen zu Benutzern
verweisen auf diese Id.

**Warum werden SQL-Parameter benutzt?** SQL-Befehl und Eingabewerte werden getrennt übertragen.
Ein Apostroph im Firmennamen bleibt dadurch ein Zeichen im Namen und wird kein SQL-Befehl.

**Was bringt `async`/`await`?** Die Anwendung wartet auf die Datenbank, ohne das Fenster während
dieser Wartezeit zu blockieren. `await` setzt die Verarbeitung fort, sobald das Ergebnis vorliegt.

**Was macht `RETURNING`?** PostgreSQL liefert nach dem Speichern die tatsächlich gespeicherten
Werte zurück, einschließlich Id und Änderungszeitpunkt. Genau diese Werte übernimmt das Dashboard.

**Warum reicht die Prüfung auf doppelte Kennungen im Formular nicht?** Zwei Kollegen könnten
gleichzeitig dieselbe Kennung eingeben. Deshalb verhindert zusätzlich ein eindeutiger Datenbankindex
doppelte Kennungen; Groß-/Kleinschreibung und äußere Leerzeichen unterscheiden sie nicht.

**Wie werden fremde Änderungen geschützt?** Beim Bearbeiten werden Id und zuletzt geladene
Version geprüft. Hat ein Kollege inzwischen gespeichert, passt die Version nicht mehr und die
Änderung wird abgewiesen. Danach muss der aktuelle Stand neu geladen werden.

**Woher kommt die Benutzerzahl?** Aus dem Zählen der passenden Benutzerzeilen in der Datenbank.
Die Zuordnung erfolgt über den Fremdschlüssel zum Unternehmen.

**Warum wird die Liste erst nach erfolgreichem Speichern geändert?** Sonst könnte die Oberfläche
ein Unternehmen anzeigen, das wegen eines Verbindungsfehlers nie dauerhaft gespeichert wurde.

## Darstellung

| Akzentfarbe | Hexwert |
| --- | --- |
| Pink-Rot | `#FF004D` |
| Blau | `#007BFF` |
| Grün | `#00C853` |
| Orange | `#FF9100` |
| Lila | `#7B2CBF` |

Akzentfarbe und Hell-/Dunkelmodus ändern auch die Formulardialoge. Auf Akzentflächen wird die
Schriftfarbe anhand des Kontrasts gewählt. In kleinen Fenstern wird die Seitenleiste ausgeblendet;
Abmelden bleibt erreichbar. Die Tabelle kann horizontal gescrollt werden, und die Kennzahlen
stehen in schmalen Fenstern untereinander.

## Prüfung und aktueller Umfang

Am 13.10.2026 wurden die Debug-Kompilierung und die Release-Kompilierung der Anwendung
erfolgreich geprüft. Die Release-Prüfung deaktivierte nur für den Prüfaufruf `PublishTrimmed`
und `PublishReadyToRun`, da deren zusätzlichen Veröffentlichungspakete lokal fehlen; die
Release-Rollenprüfung und der Ausschluss des direkten Dashboardstarts wurden mitkompiliert.
Eine optimierte Veröffentlichung war nicht Teil dieser Prüfung.

Der Authentifizierungsdienst bestand jeweils 19 lokale Prüfungen in Debug und Release.
Zusätzlich bestanden **44 Prüfungen mit der echten Neon-Datenbank**: neues Plattformkonto
ohne Firma mit BCrypt und Rolle, richtige und falsche Passwörter, deaktiviertes Konto,
entzogene Rolle, Firmenkonten, kaputte Hashwerte und gesperrte Datenzugriffe ohne Adminrechte.
Ein berechtigter Prüfadmin konnte Unternehmen dauerhaft anlegen, laden und bearbeiten.
Alle künstlichen Prüfdaten wurden entfernt. `admin@disposio.com` blieb bei diesen Prüfungen
unverändert. Das endgültige Konto wird erst angelegt, wenn sein Passwort lokal eingegeben wird.

Das Einrichtungswerkzeug wurde für .NET 8 erfolgreich mit null Fehlern und null Warnungen gebaut.

Der normale Login prüft Zugangsdaten und Rollen. Die Kundenansicht hinter **„Einloggen“** ist
weiterhin ein Platzhalter und führt keinen Wechsel in ein Kundenkonto durch. Auch das Speichern
eines Unternehmens erstellt noch keinen Kundenadmin und ersetzt keine vollständige Registrierung.
