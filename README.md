# Disposio

Disposio ist ein Dispositionssystem für **kleine Speditionen mit 2–20 Lkw und 1–3 Disponenten**.
Es bringt Disponenten und Fahrer in einem System zusammen: Touren planen, Fahrerstatus sehen und einfacher miteinander kommunizieren.

Das Projekt entsteht als Teamprojekt im Rahmen unserer Weiterbildung als Wirtschaftsinformatiker.

## Worum es geht

> Disponent plant Tour mit Stopps → weist Fahrer zu → Fahrer meldet Status mit einem Tipp →
> GPS läuft nur während der Tour → Disponent sieht Live-Karte → Chat mit Auto-Übersetzung →
> Winston schlägt bei Verspätung Nachrichten vor, der Mensch gibt frei.

### Alleinstellungsmerkmale

- **Mehrsprachigkeit:** Oberfläche und Chat werden automatisch übersetzt (Deutsch, Englisch, Polnisch). Das Original bleibt abrufbar.
- **Winston – der KI-Co-Disponent:** erkennt Verspätungen und entwirft passende Nachrichten. Ohne Freigabe durch den Disponenten wird nichts gesendet.
- **Datenschutz:** Ortung nur während einer aktiven Tour, in der Pause ist das Tracking aus.

### Rollen

| Rolle | Aufgabe |
|---|---|
| Fahrer | Tour ansehen, Status melden, chatten (Fahrer-App) |
| Disponent | Touren planen und zuweisen, Live-Karte, Chat, Vorschläge von Winston freigeben |
| Kundenadmin | verwaltet Benutzer, Rollen und Einstellungen der eigenen Firma |
| Plattformadmin | verwaltet die Kundenfirmen auf der Plattform |

## Technik

- Desktop-App mit **C#** und **WinUI 3 (XAML)**
- Datenbank und Anmeldung über **Neon** (PostgreSQL + Neon Auth)

## Projektstruktur

```
Disposio/
├── docs/                    Projektdokumentation (Obsidian)
│   ├── 00_Admin/            Organisation, lokal starten
│   ├── 01_Projekt/          Marktanalyse, USP
│   ├── 02_Anforderungen/    funktionale und nicht-funktionale Anforderungen
│   ├── 03_UseCases/         Use Cases, User Stories, Personas
│   ├── 04_Architektur/      Klassen- und Sequenzdiagramme
│   └── 06_Design/           Klickprototyp, UI-Konzepte
├── src/Disposio/            Quellcode der Desktop-App
│   └── Disposio/
│       ├── Models/          Datenklassen (Benutzer, Fahrzeug, Unternehmen …)
│       ├── Services/        Datenbankzugriff, Anmeldung, Sitzung
│       ├── Themes/          gemeinsame Farben und Stile
│       └── Views/           Seiten der App (Login, Dashboard, Firmenansicht …)
├── tests/                   Tests
└── tools/                   Hilfsprogramme (z. B. Admin-Konto einrichten)
```

Wie man die App lokal startet, steht in [`docs/00_Admin/Disposio-lokal-starten.md`](docs/00_Admin/Disposio-lokal-starten.md).

## Team

| Name | 
|---|
| Christina Kiesl |
| Niklas Bäricke |
| Pavlos Laskaridis|

## Arbeitsweise

- Wir arbeiten in **Sprints** nach **Scrumban** und planen über GitHub Issues.
- Priorisierung nach MoSCoW: Must = P0, Should = P1, Could = P2.
- Jedes Feature wird in einem **eigenen Branch** entwickelt (z. B. `fuhrpark-fahrzeugliste`).
- Änderungen kommen per **Pull Request** in `main`. Ein anderes Teammitglied schaut sie sich vorher an.

### Definition of Done

Ein Issue ist fertig, wenn:

- alle Sub-Issues erledigt sind,
- die Dokumentation verfasst ist,
- das Feature funktioniert und erfolgreich getestet wurde.
