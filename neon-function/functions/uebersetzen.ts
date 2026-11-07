// Neon Function: übersetzt eine gespeicherte Chat-Nachricht mit DeepL (D09 „Nachrichten mit Fahrer“ + Fahrer-App).
//
// Ablauf (siehe docs/04_Architektur/Chat-und-Uebersetzung.md):
//   1. Die App speichert die Nachricht ZUERST im Original (uebersetzungsstatus = 'ausstehend').
//      So geht nie Text verloren, auch wenn die Übersetzung ausfällt.
//   2. Danach ruft sie diese Funktion mit { nachrichtId } auf.
//   3. Die Funktion liest Text und Sprachen aus der Datenbank, fragt DeepL und speichert das Ergebnis:
//      'uebersetzt' (mit uebersetzungstext) oder 'fehlgeschlagen'. Ein erneuter Aufruf versucht es noch einmal.
//
// Zweite Aufgabe (D10 „Kundenkommunikation“): { vorschlagId } übersetzt den Fahrertext eines offenen
// Verspätungsvorschlags (winston_vorschlag.fahrertext_original -> fahrertext_uebersetzt), solange die
// Übersetzung noch leer ist. Die Desktop-App zeigt sie vor der Freigabe als Vorschau an.
//
// Wer darf aufrufen: jeder – die Funktion nimmt nur eine Nachrichten-Id an und übersetzt nur Nachrichten,
// die noch auf eine Übersetzung warten. Fremder Text kann so nicht übersetzt werden, und der DeepL-Schlüssel
// bleibt auf dem Server.
//
// Umgebungsvariablen:
//   DATABASE_URL   -> von Neon automatisch gesetzt
//   DEEPL_API_KEY  -> Schlüssel von https://www.deepl.com/pro-api (Free-Schlüssel enden auf ":fx")
//
// Veröffentlichen (im Ordner neon-function):
//   neon functions deploy uebersetzen --src functions/uebersetzen.ts --env DEEPL_API_KEY=<schlüssel>
//   neon functions get uebersetzen   -> URL in Services/UebersetzungDienst.cs eintragen

import { neon } from '@neondatabase/serverless';

// Werte von nachricht.uebersetzungsstatus. Die Desktop-App und die Fahrer-App verwenden dieselben.
export const STATUS = {
  ausstehend: 'ausstehend',
  uebersetzt: 'uebersetzt',
  fehlgeschlagen: 'fehlgeschlagen',
  nichtNoetig: 'nicht_noetig',
} as const;

// benutzer.sprache -> DeepL-Sprachcodes
const QUELLE: Record<string, string> = { de: 'DE', en: 'EN', pl: 'PL' };
const ZIEL: Record<string, string> = { de: 'DE', en: 'EN-GB', pl: 'PL' };

const CORS = {
  'access-control-allow-origin': '*',
  'access-control-allow-methods': 'POST, OPTIONS',
  'access-control-allow-headers': 'content-type',
};

export type Abfrage = (query: string, params: unknown[]) => Promise<Record<string, any>[]>;
export type DeepL = (text: string, quelle: string, ziel: string) => Promise<string>;

// Der echte DeepL-Aufruf. Free-Schlüssel (":fx") gehen an api-free, alle anderen an die Pro-Adresse.
export function deeplMitSchluessel(schluessel: string): DeepL {
  const url = schluessel.endsWith(':fx')
    ? 'https://api-free.deepl.com/v2/translate'
    : 'https://api.deepl.com/v2/translate';
  return async (text, quelle, ziel) => {
    const res = await fetch(url, {
      method: 'POST',
      headers: { authorization: `DeepL-Auth-Key ${schluessel}`, 'content-type': 'application/json' },
      body: JSON.stringify({ text: [text], source_lang: quelle, target_lang: ziel }),
      signal: AbortSignal.timeout(10_000),
    });
    if (!res.ok) throw new Error(`DeepL antwortet mit ${res.status}`);
    const data: any = await res.json();
    const ergebnis = data?.translations?.[0]?.text;
    if (typeof ergebnis !== 'string' || ergebnis.length === 0) throw new Error('DeepL lieferte keinen Text');
    return ergebnis;
  };
}

export async function uebersetzen(nachrichtId: number, abfrage: Abfrage, deepl: DeepL | null) {
  const rows = await abfrage(
    `SELECT nachrichtentext, originalsprache, zielsprache, uebersetzungsstatus, uebersetzungstext
     FROM public.nachricht WHERE nachricht_id = $1::bigint`, [nachrichtId]);
  if (rows.length === 0) return { status: 404, body: { fehler: 'Nachricht nicht gefunden.' } };

  const n = rows[0];
  // Schon fertig (oder keine Übersetzung nötig): einfach den Stand zurückgeben.
  if (n.uebersetzungsstatus !== STATUS.ausstehend && n.uebersetzungsstatus !== STATUS.fehlgeschlagen)
    return { status: 200, body: { status: n.uebersetzungsstatus, uebersetzung: n.uebersetzungstext } };

  const quelle = QUELLE[n.originalsprache];
  const ziel = ZIEL[n.zielsprache];
  let uebersetzung: string | null = null;
  if (quelle && ziel && quelle === ziel.slice(0, 2)) {
    uebersetzung = n.nachrichtentext; // gleiche Sprache
  } else if (quelle && ziel && deepl) {
    try {
      uebersetzung = await deepl(n.nachrichtentext, quelle, ziel);
    } catch (fehler) {
      console.error('uebersetzen: DeepL', (fehler as Error)?.message); // ohne Nachrichtentext loggen
    }
  }

  // Nur ändern, wenn die Nachricht noch wartet (zwei gleichzeitige Aufrufe überschreiben sich nicht).
  const neuerStatus = uebersetzung === null ? STATUS.fehlgeschlagen : STATUS.uebersetzt;
  await abfrage(
    `UPDATE public.nachricht SET uebersetzungstext = $2, uebersetzungsstatus = $3
     WHERE nachricht_id = $1::bigint AND uebersetzungsstatus IN ('ausstehend', 'fehlgeschlagen')`,
    [nachrichtId, uebersetzung, neuerStatus]);

  return uebersetzung === null
    ? { status: 502, body: { status: neuerStatus, fehler: 'Die Übersetzung ist gerade nicht möglich.' } }
    : { status: 200, body: { status: neuerStatus, uebersetzung } };
}

// Fahrertext eines offenen Winston-Vorschlags übersetzen. Nur wenn die Übersetzung leer ist.
export async function vorschlagUebersetzen(vorschlagId: number, abfrage: Abfrage, deepl: DeepL | null) {
  const rows = await abfrage(
    `SELECT fahrertext_original, ausgangssprache, fahrersprache, fahrertext_uebersetzt, status
     FROM public.winston_vorschlag WHERE vorschlag_id = $1::bigint`, [vorschlagId]);
  if (rows.length === 0) return { status: 404, body: { fehler: 'Vorschlag nicht gefunden.' } };

  const v = rows[0];
  if (v.status !== 'offen' || v.fahrertext_uebersetzt)
    return { status: 200, body: { uebersetzung: v.fahrertext_uebersetzt } };

  const quelle = QUELLE[v.ausgangssprache];
  const ziel = ZIEL[v.fahrersprache];
  let uebersetzung: string | null = null;
  if (quelle && ziel && quelle === ziel.slice(0, 2)) {
    uebersetzung = v.fahrertext_original;
  } else if (quelle && ziel && deepl) {
    try {
      uebersetzung = await deepl(v.fahrertext_original, quelle, ziel);
    } catch (fehler) {
      console.error('uebersetzen (Vorschlag): DeepL', (fehler as Error)?.message);
    }
  }
  if (uebersetzung === null) return { status: 502, body: { fehler: 'Die Übersetzung ist gerade nicht möglich.' } };

  await abfrage(
    `UPDATE public.winston_vorschlag SET fahrertext_uebersetzt = $2
     WHERE vorschlag_id = $1::bigint AND status = 'offen' AND fahrertext_uebersetzt IS NULL`,
    [vorschlagId, uebersetzung]);
  return { status: 200, body: { uebersetzung } };
}

export async function bearbeiten(request: Request, abfrage: Abfrage, deepl: DeepL | null): Promise<Response> {
  const antwort = (status: number, body: unknown) => Response.json(body, { status, headers: CORS });
  if (request.method === 'OPTIONS') return new Response(null, { status: 204, headers: CORS });
  if (request.method !== 'POST') return antwort(405, { fehler: 'Nur POST.' });

  let body: any;
  try { body = JSON.parse(await request.text()); } catch { return antwort(400, { fehler: 'Ungültige Anfrage.' }); }
  const vorschlagId = Number(body?.vorschlagId);
  const id = Number(body?.nachrichtId);
  const istVorschlag = Number.isSafeInteger(vorschlagId) && vorschlagId > 0;
  if (!istVorschlag && (!Number.isSafeInteger(id) || id <= 0))
    return antwort(400, { fehler: 'nachrichtId oder vorschlagId fehlt.' });

  try {
    const ergebnis = istVorschlag
      ? await vorschlagUebersetzen(vorschlagId, abfrage, deepl)
      : await uebersetzen(id, abfrage, deepl);
    return antwort(ergebnis.status, ergebnis.body);
  } catch (fehler) {
    console.error('uebersetzen:', (fehler as Error)?.message);
    return antwort(500, { fehler: 'Interner Fehler.' });
  }
}

const sql = neon(process.env.DATABASE_URL!);
const neonAbfrage: Abfrage = (query, params) => sql.query(query, params) as Promise<Record<string, any>[]>;
const deepl = process.env.DEEPL_API_KEY ? deeplMitSchluessel(process.env.DEEPL_API_KEY) : null;

export default {
  fetch: (request: Request) => bearbeiten(request, neonAbfrage, deepl),
};
