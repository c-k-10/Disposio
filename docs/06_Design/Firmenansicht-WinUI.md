# Firmenansicht: Grundgerüst und Rollen

Die Firmenansicht ist der Arbeitsbereich für alle Konten eines Kundenunternehmens. Sie liegt in
`src/Disposio/Disposio/Views/FirmenAnsicht/`. Links steht das Menü je Rolle, rechts die gewählte
Ansicht. Die Ansichten tragen dieselben Wireframe-IDs wie der Prototyp (K01, D04 …).

## Stand

- Alle 30 Firmenansichten sind angelegt und erreichbar. Unteransichten wie K02 oder D05 stehen
  nicht im Menü, sondern werden über Knöpfe in ihrer Oberansicht geöffnet.
- **K03 „Rollen & Rechte“** zeigt bereits echte Inhalte: alle Rollen mit Bereich und Aufgaben.
  Die eigenen Rollen des angemeldeten Kontos sind markiert.
- Alle anderen Ansichten sind Platzhalter. Sie zeigen Titel, Zweck und Wireframe-ID und werden
  einzeln mit Inhalt gefüllt.
- Es werden noch keine Firmendaten geladen. Jede Ansicht, die später Daten lädt oder speichert,
  muss vorher die Rolle erneut in der Datenbank prüfen (wie das Dashboard mit `DashboardZugriffPruefenAsync`).

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
- `BenutzerSitzung.AktuelleFirmaId` liefert die Firma, die gerade angezeigt wird: beim Firmenkonto
  die eigene, beim Admin-Zugang die gewählte. Spätere Ansichten laden ihre Daten mit dieser Id.

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
| `Views/FirmenAnsicht/FirmenAnsicht.xaml` | Aussehen: Seitenleiste, Menübereich, Kopfzeile und Inhaltsbereich. |
| `Views/FirmenAnsicht/FirmenAnsicht.xaml.cs` | Baut das Menü je Rolle, wechselt die Ansichten, prüft den Zugriff und meldet ab. |
| `Services/AuthService.cs` | Liefert bei Firmenkonten zusätzlich Firmenname, Abteilung und die erkannten Firmenrollen. |
| `Services/BenutzerSitzung.cs` | `FirmenansichtErlaubt`, der Admin-Zugang `Firmenzugang` und die Debug-Vorschau `FirmenVorschauStarten`. |
| `Services/UnternehmenRepository.cs` | `FirmenzugangOeffnenAsync`: Rolle prüfen, Firma lesen und den Admin-Zugang protokollieren. |
| `Views/DashboardAnsicht/DashboardAnsicht.xaml.cs` | „Einloggen“ öffnet nach Bestätigung die Firmenansicht der gewählten Firma. |

## Nächste Schritte

- Die Ansichten einzeln mit Daten füllen, beginnend mit K01/K02 (Benutzer verwalten): Erst dann
  kann ein Kundenadmin selbst Konten anlegen und Rollen vergeben.
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
