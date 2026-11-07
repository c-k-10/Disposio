# Chat Disponent ↔ Fahrer mit Übersetzung

Gilt für D09 „Nachrichten mit Fahrer“ (Desktop-App) und den Chat in der Fahrer-App. Beide Seiten müssen sich daran halten.

## Grundregel

**Erst speichern, dann übersetzen.** Jede Nachricht wird sofort im Original in `nachricht` gespeichert.
Die Übersetzung kommt danach dazu. Fällt sie aus, bleibt das Original sichtbar – es geht nie Text verloren.

```
App/Fahrer-App ── INSERT nachricht (uebersetzungsstatus = 'ausstehend') ──► Neon
App/Fahrer-App ── POST { nachrichtId } ──► Neon Function „uebersetzen“ ── DeepL ──► UPDATE nachricht
```

## Tabellen

**chat** – genau ein Chat je Tour und Fahrer.

| Spalte | Wert |
|---|---|
| `tour_id` | die Tour |
| `fahrer_benutzer_id` | `benutzer_id` des Fahrers der Tour (`fahrer.benutzer_id`) |
| `disponent_benutzer_id` | Disponent, der den Chat angelegt hat. Alle Disponenten der Firma sehen und schreiben im Chat. |
| `status` | `'offen'` |

Gibt es zur Tour und zum Fahrer noch keinen Chat, legt ihn an, wer zuerst schreibt.

**nachricht**

| Spalte | Wert |
|---|---|
| `nachrichtentext` | Originaltext, so wie getippt (max. 2000 Zeichen) |
| `originalsprache` | `benutzer.sprache` des Absenders: `de`, `en` oder `pl` |
| `zielsprache` | `benutzer.sprache` des Empfängers (bei Fahrer-Nachrichten: des Disponenten, der den Chat angelegt hat) |
| `uebersetzungsstatus` | siehe unten |
| `uebersetzungstext` | Übersetzung in `zielsprache`, von der Neon Function gesetzt |
| `uebertragungsstatus` | `'gesendet'` |
| `benutzer_id` | Absender |
| `gelesen`, `gelesen_am` | Empfänger setzt beim Öffnen auf `true` / `now()` |

**uebersetzungsstatus**

| Wert | Bedeutung | Anzeige |
|---|---|---|
| `nicht_noetig` | Absender und Empfänger sprechen dieselbe Sprache | Original |
| `ausstehend` | gespeichert, Übersetzung läuft | Original + „Übersetzung läuft …“ |
| `uebersetzt` | `uebersetzungstext` ist gesetzt | Übersetzung, Original aufklappbar |
| `fehlgeschlagen` | DeepL nicht erreichbar | Original + „Erneut übersetzen“ |

## Zugestellt und gelesen

Die **App des Empfängers** meldet zurück, was mit einer Nachricht passiert ist. Die Desktop-App zeigt das bei eigenen
Nachrichten an (wird alle 15 Sekunden neu geladen):

| Anzeige in D09 | Datenbank | Wer setzt es, wann |
|---|---|---|
| ✓ Gesendet | `uebertragungsstatus = 'gesendet'` | Absender beim Speichern |
| ✓✓ Zugestellt | `uebertragungsstatus = 'zugestellt'` | Fahrer-App, sobald sie die Nachricht vom Server geladen hat |
| ✓✓ Gelesen 06:25 | `gelesen = true`, `gelesen_am = now()` | Fahrer-App, sobald der Fahrer den Chat geöffnet hat |

Für die Fahrer-App (Nachrichten an den Fahrer = alle Nachrichten im Chat, die nicht vom Fahrer selbst sind):

```sql
-- nach dem Laden der Nachrichten
UPDATE public.nachricht SET uebertragungsstatus = 'zugestellt'
WHERE chat_id = $1 AND benutzer_id <> $2 AND uebertragungsstatus = 'gesendet';
-- wenn der Chat auf dem Bildschirm ist ($2 = benutzer_id des Fahrers)
UPDATE public.nachricht SET uebertragungsstatus = 'zugestellt', gelesen = true, gelesen_am = now()
WHERE chat_id = $1 AND benutzer_id <> $2 AND NOT gelesen;
```

Umgekehrt macht es die Desktop-App genauso: Öffnet ein Disponent den Chat, werden die Nachrichten des Fahrers als
gelesen markiert.

**Ohne Fahrer-App testen** (im Neon SQL Editor; nimmt automatisch die neueste Nachricht eines Disponenten):

```sql
-- ✓✓ Zugestellt
UPDATE public.nachricht SET uebertragungsstatus = 'zugestellt'
WHERE nachricht_id = (SELECT n.nachricht_id FROM public.nachricht n JOIN public.chat c ON c.chat_id = n.chat_id
                      WHERE n.benutzer_id <> c.fahrer_benutzer_id ORDER BY n.nachricht_id DESC LIMIT 1);
-- ✓✓ Gelesen
UPDATE public.nachricht SET uebertragungsstatus = 'zugestellt', gelesen = true, gelesen_am = now()
WHERE nachricht_id = (SELECT n.nachricht_id FROM public.nachricht n JOIN public.chat c ON c.chat_id = n.chat_id
                      WHERE n.benutzer_id <> c.fahrer_benutzer_id ORDER BY n.nachricht_id DESC LIMIT 1);
```

## Neon Function `uebersetzen`

- Datei: `neon-function/functions/uebersetzen.ts`
- Aufruf: `POST` mit JSON `{ "nachrichtId": 123 }`, ohne Anmeldung
- Übersetzt nur Nachrichten mit Status `ausstehend` oder `fehlgeschlagen`. Ein zweiter Aufruf ist also ein „Erneut versuchen“.
- Antwort `200 { status, uebersetzung }` oder `502 { status: "fehlgeschlagen", fehler }`
- Der DeepL-Schlüssel liegt nur als Umgebungsvariable `DEEPL_API_KEY` auf dem Server.
- Zweite Aufgabe für D10 „Kundenkommunikation“: `POST { "vorschlagId": 7 }` übersetzt den Fahrertext eines offenen
  Winston-Vorschlags (`winston_vorschlag.fahrertext_original` → `fahrertext_uebersetzt`). Nach „Freigeben & senden“
  landet dieser Text als normale Chat-Nachricht beim Fahrer.

Veröffentlichen (im Ordner `neon-function`):

```
neon functions deploy uebersetzen --src functions/uebersetzen.ts --env DEEPL_API_KEY=<schlüssel>
neon functions get uebersetzen
```

Die URL steht in der Desktop-App in `Services/UebersetzungDienst.cs`.
