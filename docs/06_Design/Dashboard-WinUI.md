# Dashboard: Bedienung und Datenbank verstehen

Die Kundenübersicht liegt in `src/Disposio/Disposio/Views/DashboardAnsicht/`.
Unternehmen werden in der vorhandenen Neon-Datenbank dauerhaft gespeichert. Die Anwendung
liest die Tabelle `public.unternehmen`; sie legt keine Beispieldaten beim Start an.

## Starten und bedienen

- In Visual Studio oben neben dem Startpfeil **„Disposio (Dashboard)“** auswählen und F5 drücken.
  Dieses Startprofil öffnet das Dashboard ohne Anmeldedaten.
- „Disposio (Package)“ und „Disposio (Unpackaged)“ starten weiterhin mit der Login-Seite.
  Das Dashboard-Startprofil übergibt `--dashboard`; `App.xaml.cs` liest diesen Startparameter.
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
- „Abmelden“ kehrt zum Login zurück. Akzentfarbe und Hell-/Dunkelmodus werden übernommen.

## Welche Datei macht was?

| Datei | Aufgabe |
| --- | --- |
| `Views/DashboardAnsicht/DashboardAnsicht.xaml` | Beschreibt das Aussehen: Liste, Kennzahlen, Buttons und Farben. |
| `Views/DashboardAnsicht/DashboardAnsicht.xaml.cs` | Reagiert auf Klicks, prüft Formulare und aktualisiert die Anzeige. |
| `Models/Unternehmen.cs` | Bündelt die Unternehmensdaten. Ein Entwurf enthält Eingaben; ein gespeicherter Datensatz enthält zusätzlich Id, Benutzerzahl und Version. |
| `Services/UnternehmenRepository.cs` | Führt die SQL-Abfragen zum Laden, Anlegen und Bearbeiten aus. |
| `Services/NeonSqlClient.cs` | Überträgt SQL und getrennte Parameter über Neons HTTPS-Schnittstelle. |
| `Services/Database.cs` | Stellt die bereits vorhandene Verbindungseinstellung bereit. |
| `Services/001_unternehmen_dashboard.sql` | Enthält die einmalige Ergänzung der vorhandenen Tabelle. |

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

## Fragen fürs Fachgespräch

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

Am 13.10.2026 wurde das Originalprojekt für Debug/x64 erfolgreich ohne gemeldete Fehler oder
Warnungen gebaut und über „Disposio (Dashboard)“ in Visual Studio Insiders gestartet.

Die echte Neon-Datenbank wurde mit eindeutig markierten künstlichen Datensätzen geprüft:
Anlegen einschließlich Kontaktdaten und Apostroph im Firmennamen, erneutes Laden mit einem neuen
Repository, Bearbeiten, doppelte Kennungen beim Anlegen und Bearbeiten sowie Abweisen einer
veralteten Version. Fehlgeschlagene Schreibvorgänge ließen den bestätigten Datensatz unverändert.
Die Passwortregel und die echte Benutzerzahl wurden ebenfalls geprüft.

In der laufenden Oberfläche wurden die leere Liste mit drei Nullen, die Pflichtfeldmeldung,
das Anlegen, das Wiederfinden nach einem vollständigen Neustart und der Statuswechsel von
„Testphase“ auf „Aktiv“ geprüft. Dabei änderte sich die Testphase-Kennzahl von 1 auf 0.
Die Kontakt-E-Mail wurde beim leeren optionalen Feld korrekt aus der Admin-E-Mail übernommen.
Fehlermeldung und Buttons bleiben im langen Formular außerhalb des scrollbaren Feldbereichs.
Alle für diese Prüfungen angelegten Datensätze wurden anschließend entfernt.

Die Kundenansicht hinter „Einloggen“ und eine echte Prüfung von Login-Zugangsdaten sind weiterhin
nicht angebunden. Das Speichern eines Unternehmens stellt noch keine vollständige Registrierung dar.
