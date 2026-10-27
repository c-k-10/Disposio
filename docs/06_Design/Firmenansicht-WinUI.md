# Firmenansicht: Grundgerüst und Rollen

Die Firmenansicht ist der Arbeitsbereich für alle Konten eines Kundenunternehmens. Sie liegt in
`src/Disposio/Disposio/Views/FirmenAnsicht/`. Links steht das Menü je Rolle, rechts die gewählte
Ansicht. Die Ansichten tragen dieselben Wireframe-IDs wie der Prototyp (K01, D04 …).

## Stand

- Alle 30 Firmenansichten sind angelegt und erreichbar. Unteransichten wie K02 oder D05 stehen
  nicht im Menü, sondern werden über Knöpfe in ihrer Oberansicht geöffnet.
- **K03 „Rollen & Rechte“** zeigt bereits echte Inhalte: alle Rollen mit Bereich und Aufgaben.
  Die eigenen Rollen des angemeldeten Kontos sind markiert. K03 liegt in einer eigenen Datei und
  ist das Beispiel dafür, wie eine fertige Ansicht aufgebaut ist.
- **F01 „Fuhrparkübersicht“** lädt die Fahrzeuge der Firma aus `public.fahrzeug`. **F02** legt ein
  Fahrzeug an (Bearbeiten folgt). Kennzeichen werden einheitlich gespeichert (Großbuchstaben, ohne
  doppelte Leerzeichen); ein Kennzeichen, das es in der Firma schon gibt, lehnt die Datenbank ab.
  Jedes neue Fahrzeug erzeugt einen Eintrag „Fahrzeug angelegt“ im Aktionsprotokoll.
- Alle anderen Ansichten sind Platzhalter. Sie zeigen Titel, Zweck und Wireframe-ID und werden
  einzeln mit Inhalt gefüllt (siehe „So legt man eine neue Ansicht an“).
- **G02 „Kennzahlen & Statistik“** zeigt je Kalendermonat Touren, Aufträge, Tankvorgänge (Liter,
  Kosten), Wartungen, Schäden, Gesamtkosten und protokollierte Aktionen. Unter „Downloads“ steht jeder
  Monat (zum Beispiel „Okt 26“) mit „PDF herunterladen“; die Datei landet im Ordner „Downloads“.
  Das PDF erzeugt `Services/StatistikPdf.cs` ohne zusätzliche Bibliothek.
- **Gesperrte Firma** (Status „Inaktiv“ im Admin-Dashboard): Inhaber/Geschäftsführer und Kundenadmin
  sehen nur noch G02 („Nur Statistik“), alle anderen Konten werden beim Login abgewiesen.
- Jede Ansicht, die Firmendaten lädt oder speichert, prüft vorher mit
  `AuthService.FirmenZugriffPruefenAsync` die Rolle erneut in der Datenbank.

## So legt man eine neue Ansicht an

Jeder Rollenbereich hat einen eigenen Ordner unter `Views/FirmenAnsicht/`. Wer an einem Bereich
arbeitet, ändert nur Dateien in diesem Ordner. So kommen sich parallele Branches nicht in die Quere.

| Ordner | Ansichten | Eintragungsdatei |
| --- | --- | --- |
| `Kundenadmin/` | K01–K04 | `KundenadminAnsichten.cs` |
| `Fuhrpark/` | F01–F05 | `FuhrparkAnsichten.cs` |
| `Disposition/` | D01–D15 | `DispositionAnsichten.cs` |
| `Abteilung/` | L01–L06 | `AbteilungAnsichten.cs` |
| `Geschaeftsfuehrung/` | G01–G02 | `GeschaeftsfuehrungAnsichten.cs` |

1. Im passenden Ordner ein **Benutzersteuerelement (WinUI)** anlegen, benannt nach der
   Wireframe-ID, zum Beispiel `F01Fuhrpark.xaml`. Vorbild: `Kundenadmin/K03RollenUndRechte.xaml`.
2. Der Konstruktor bekommt den `FirmenAnsichtKontext`. Darüber gibt es `Oeffnen("F02", fahrzeugId)`
   für andere Ansichten, den Startwert `Parameter`, `IstVorschau` und `ZugriffBeenden()`.
3. In der Eintragungsdatei des Ordners **eine Zeile** ergänzen:
   `"F01" => new F01Fuhrpark(kontext),`
4. Für Farben und Knöpfe die gemeinsamen Stile aus `Themes/FirmenStil.xaml` verwenden, zum Beispiel
   `Style="{StaticResource KarteStyle}"`, `PrimaryButton`, `FormTextBox`, `HinweisText`, `FehlerText`.
   Dann passen Hell/Dunkel und Akzentfarbe automatisch.

**Firmendaten laden oder speichern:** SQL gehört wie beim Dashboard in ein Repository unter
`Services/`. Jede Methode prüft zuerst die Rolle und verwendet danach nur die zurückgegebene Firma:

```csharp
public async Task<IReadOnlyList<Fahrzeug>> LadenAsync(CancellationToken cancellationToken = default)
{
    // Prüft in Neon: Konto aktiv, Rolle vorhanden (beim Admin-Zugang: Admin-Rolle).
    FirmenZugriff zugriff = await AuthService.FirmenZugriffPruefenAsync(FirmenRolle.Fuhrparkmanager, cancellationToken);
    // Nur zugriff.UnternehmenId verwenden, nie eine Firmen-Id aus der Oberfläche.
    var rows = await _client.Value.QueryAsync(
        "SELECT … FROM public.fahrzeug WHERE unternehmen_id = $1::bigint ORDER BY kennzeichen",
        new string?[] { zugriff.UnternehmenId.ToString(CultureInfo.InvariantCulture) }, cancellationToken);
    …
}
```

In der Ansicht selbst:

- `IstVorschau` zuerst abfragen und dann statt Daten einen Hinweis zeigen. In der Vorschau gibt es keine Firma.
- `UnauthorizedAccessException` abfangen und `kontext.ZugriffBeenden()` aufrufen: Die Rolle wurde
  entzogen oder das Konto deaktiviert, die Anmeldung erscheint.
- Andere Fehler als verständlichen Text anzeigen, keine technischen Meldungen.
- Beim Ändern und Löschen gehört `unternehmen_id = $…` immer mit in die `WHERE`-Bedingung.
  So kann niemand Datensätze einer fremden Firma ändern, auch nicht mit einer falschen Id.

## Rollen

Die Rollen standen schon vorher in `public.rolle`. Die App erkennt sie über ihre genaue Bezeichnung:

| Rolle in der App | Bezeichnung in `public.rolle` | Menü |
| --- | --- | --- |
| Disponent | `Disponent` | D01, D02, D04, D08, D09, D10, D11, D15 |
| Fuhrparkmanager | `Fuhrparkmanager` | F01, F03, F04 |
| Abteilungsleiter | `Abteilungsleiter` | L01, L05, L02, L03, L06 |
| Inhaber / Geschäftsführer | `Inhaber/Geschäftsführer` | G01, G02 |
| Kundenadmin | `Kundenadmin` | K01, K03, K04 |
| Fahrer | `Fahrer` | keins (Fahrer-App) |

- Ein Konto kann mehrere Rollen haben. Das Menü zeigt dann für jede Rolle einen eigenen Abschnitt.
  Die Startansicht ist der erste Menüpunkt der ersten Rolle in der Tabelle oben.
- Nur Konten **mit Firma** bekommen Firmenrollen. Ein Plattformkonto (MegakrassenAdmin) hat keine
  Firma und öffnet weiterhin nur das Admin-Dashboard.
- Unbekannte Rollennamen werden ignoriert und erteilen keine Rechte. Die alte, ungenutzte Rolle
  `MegaKrasserAdmin` (ID 7) spielt deshalb keine Rolle.

## Anmeldung

```text
Firmenkonto: Kennung, E-Mail und Passwort eingeben
    ↓
AuthService prüft Passwort und liest Rollen, Firmenname und Abteilung
    ↓
Mindestens eine Büro-Rolle? → Firmenansicht öffnet sich
Nur „Fahrer“?               → Hinweis auf die Fahrer-App, keine Sitzung
Keine Rolle?                → Hinweis „an den Kundenadmin wenden“, keine Sitzung
```

Auch beim Öffnen einer einzelnen Ansicht wird geprüft, ob das Konto die passende Rolle hat.
Sonst erscheint S03 „Kein Zugriff“.

## Admin-Zugang aus dem Dashboard

Ein MegakrassenAdmin kann im Admin-Dashboard bei jedem Kunden auf **„Einloggen“** klicken und
sieht dann die Firmenansicht dieser Firma mit allen Büro-Rollen.

```text
„Einloggen“ beim Kunden klicken → Bestätigungsdialog „Firmenansicht öffnen“
    ↓
Repository prüft die Admin-Rolle erneut in Neon und liest die Firma neu
    ↓
Im selben SQL-Befehl: Eintrag „Firmenansicht als Admin geöffnet“ in public.aktionsprotokoll
    ↓
Sitzung merkt sich die Firma (BenutzerSitzung.Firmenzugang) → Firmenansicht öffnet sich
    ↓
„Zurück zum Admin-Dashboard“ beendet den Zugang; „Abmelden“ beendet die ganze Sitzung
```

- Der Admin bleibt mit **seinem eigenen Konto** angemeldet. Es wird kein Kundenkonto und kein
  Kundenpasswort benutzt. Deshalb steht im Protokoll auch sein Name.
- Oben in der Firmenansicht steht ein Hinweis, dass man eine fremde Firma als MegakrassenAdmin sieht.
- `AuthService.FirmenZugriffPruefenAsync` liefert die Firma, deren Daten gezeigt werden dürfen: beim
  Firmenkonto die eigene, beim Admin-Zugang die gewählte (siehe „So legt man eine neue Ansicht an“).

Alle Admin-Zugänge einer Firma nachsehen:

```sql
SELECT p.zeitpunkt, b.vorname || ' ' || b.nachname AS admin, u.firmenname
FROM public.aktionsprotokoll p
LEFT JOIN public.benutzer b ON b.benutzer_id = p.benutzer_id
JOIN public.unternehmen u ON u.unternehmen_id = p.unternehmen_id
WHERE p.aktion = 'Firmenansicht als Admin geöffnet'
ORDER BY p.zeitpunkt DESC;
```

## Vorschau ohne Anmeldung

Das Visual-Studio-Profil **„Disposio (Firmenansicht)“** startet mit `--firmenansicht` und zeigt
alle Büro-Rollen, ohne dass ein Testkonto nötig ist. Wie beim Dashboard gilt das nur im
**Debug-Build**; im Release-Build führt auch dieser Aufruf zur Anmeldung. Die Vorschau ist als
„Entwicklungsansicht“ gekennzeichnet und lädt keine Firmendaten.

## Welche Datei macht was?

| Datei | Aufgabe |
| --- | --- |
| `Models/FirmenRolle.cs` | Die sechs Firmenrollen, ihre Bezeichnungen in der Datenbank und ihre Aufgaben. |
| `Models/AnsichtenKatalog.cs` | Alle Firmenansichten mit Wireframe-ID, Titel, Zweck, Rolle und Oberansicht. Daraus entsteht das Menü. |
| `Views/FirmenAnsicht/FirmenAnsicht.xaml` | Aussehen des Rahmens: Seitenleiste, Menübereich, Kopfzeile und Inhaltsbereich. |
| `Views/FirmenAnsicht/FirmenAnsicht.xaml.cs` | Baut das Menü je Rolle, holt die Ansicht aus der Eintragungsdatei ihres Bereichs, prüft den Zugriff und meldet ab. |
| `Views/FirmenAnsicht/FirmenAnsichtKontext.cs` | Was jede Ansicht vom Rahmen bekommt: Ansicht, Rollen, Startwert, `Oeffnen`, `ZugriffBeenden`. |
| `Views/FirmenAnsicht/<Bereich>/` | Die Ansichten eines Rollenbereichs und ihre Eintragungsdatei. |
| `Themes/FirmenStil.xaml` | Gemeinsame Farben und Stile aller Firmenansichten, in `App.xaml` eingebunden. |
| `Services/AuthService.cs` | Liefert bei Firmenkonten Firmenname, Abteilung und Firmenrollen. `FirmenZugriffPruefenAsync` prüft vor jedem Datenzugriff die Rolle. |
| `Services/BenutzerSitzung.cs` | `FirmenansichtErlaubt`, der Admin-Zugang `Firmenzugang` und die Debug-Vorschau `FirmenVorschauStarten`. |
| `Services/StatistikRepository.cs`, `Services/StatistikPdf.cs`, `Models/Statistik.cs` | Monatsstatistik laden (auch bei gesperrter Firma, nur lesend) und als PDF speichern. |
| `Views/FirmenAnsicht/Geschaeftsfuehrung/G02Statistik.xaml` | Kennzahlen je Monat und die Download-Liste. |
| `Services/UnternehmenRepository.cs` | `FirmenzugangOeffnenAsync`: Rolle prüfen, Firma lesen und den Admin-Zugang protokollieren. |
| `Views/DashboardAnsicht/DashboardAnsicht.xaml.cs` | „Einloggen“ öffnet nach Bestätigung die Firmenansicht der gewählten Firma. |
| `Models/Fahrzeug.cs` | Fahrzeug, Formulareingaben (`FahrzeugEntwurf`) und die Auswahllisten für Klasse, Aufbau und Status. |
| `Services/FahrzeugRepository.cs` | Fahrzeuge und Abteilungen laden, Fahrzeug anlegen. Prüft Rolle und Eingaben, protokolliert das Anlegen. |
| `Views/FirmenAnsicht/Fuhrpark/F01Fuhrpark.xaml` | Fahrzeugliste mit „Erneut laden“ und „+ Fahrzeug anlegen“. |
| `Views/FirmenAnsicht/Fuhrpark/F02FahrzeugFormular.xaml` | Formular „Fahrzeug anlegen“ mit Pflichtfeldprüfung. |

## Nächste Schritte

- Die Ansichten einzeln mit Daten füllen: K01–K04 (Benutzer verwalten) und F02 „Bearbeiten“
  als Nächstes, weil Touren Fahrer und Fahrzeuge brauchen.
- Jede Tour gehört zu einer Abteilung (`tour.abteilung_id` ist Pflicht). Abteilungen anzulegen
  passt zum Kundenadmin (K02 „Abteilungszugriff“).
- Für echte Anmeldetests je Rolle Testkonten anlegen (Schreibzugriff auf Neon, vorher absprechen).
- S02 „Passwort zurücksetzen“ ist noch nicht angebunden.

## Fragen fürs Fachgespräch

**Warum steht die Rollenliste im Code und in der Datenbank?** Die Datenbank speichert, welches
Konto welche Rolle hat. Der Code legt fest, welche Ansichten zu einer Rolle gehören. Beide
verbinden sich über die genaue Rollenbezeichnung.

**Was passiert bei mehreren Rollen?** Das Menü fügt die Bereiche aller Rollen zusammen. Eine
Inhaberin, die zugleich Kundenadmin und Disponentin ist, sieht alle drei Abschnitte.

**Warum reicht das Ausblenden im Menü nicht als Schutz?** Eine Ansicht könnte auch über einen
anderen Weg geöffnet werden. Deshalb prüft die Seite beim Öffnen jeder Ansicht die Rolle erneut.
Für Daten muss zusätzlich die Datenbank prüfen, solange es noch kein eigenes Backend gibt.
