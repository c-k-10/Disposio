// Neon Function: nimmt Sendungen von der Webseite an (Disposio.web/anmelden.html).
//
// Wer darf aufrufen: JEDER – die Funktion ist öffentlich, ohne Anmeldung. Deshalb gilt:
//   - Alle Eingaben werden hier vollständig geprüft (gleiche Regeln wie SendungRepository.Pruefen in der App).
//   - Die Firma kommt nur über ihren zufälligen anmelde_code (Migration 009). Gesperrte Firmen werden abgelehnt.
//   - Pro Firma höchstens MAX_PRO_STUNDE Web-Sendungen pro Stunde (Schutz gegen Spam).
//   - Die Sendung bekommt den Status „Angefragt“; der Disponent prüft sie in D02.
//
// Zwei Aufrufe:
//   GET  -> Liste der Speditionen für das Auswahlfeld im Formular: { firmen: [{ code, name }] }
//   POST -> neue Sendung anlegen
//
// Jede Webseite darf die Funktion aufrufen (CORS "*"). Eine Beschränkung auf bestimmte Adressen
// schützt hier nichts, weil man die Funktion auch ohne Browser aufrufen kann – der Schutz sind die
// Prüfungen, der anmelde_code und die Stundengrenze.
//
// Umgebungsvariablen:
//   DATABASE_URL   -> von Neon automatisch gesetzt
//
// Veröffentlichen (im Ordner neon-function):
//   neon functions deploy sendung --src functions/sendung.ts
//   neon functions get sendung     -> URL in Disposio.web/js/config.js (sendungApiUrl) eintragen

import { neon } from '@neondatabase/serverless';

const MAX_PRO_STUNDE = 30;
const MAX_BODY_BYTES = 20_000;

// Gleiche Auswahlwerte wie SendungWerte in Models/SendungFormular.cs
const GUTARTEN = ['Trockengut', 'Frische'];
const VERPACKUNGEN = ['Europalette', 'Einwegpalette', 'Gitterbox', 'Karton', 'Sonstiges'];
const EQUIPMENT = ['Keines', 'Hebebühne', 'Stapler vor Ort', 'Mitnahmestapler', 'Hubwagen'];

export class Eingabefehler extends Error {}

// ─────────────────────────────────────────────────────────────────────────────
// 1. Eingaben prüfen. Gibt ein sauberes Objekt zurück oder wirft Eingabefehler.
// ─────────────────────────────────────────────────────────────────────────────
export interface Sendung {
  firma: string;
  kunde: { firmenname: string; ansprechpartner: string; email: string; telefon: string;
           adresse: string; plz: string; ort: string };
  kundenreferenz: string | null;
  abholadresse: string; abholdatum: string; abholVon: string; abholBis: string;
  lieferadresse: string; lieferdatum: string; lieferVon: string; lieferBis: string;
  ansprechpartner: string;
  avisierung: boolean;
  gutart: string; verpackungstyp: string; anzahl: number; gewichtKg: number;
  laengeM: number; breiteM: number; hoeheM: number;
  temperaturMinC: number | null; temperaturMaxC: number | null;
  equipment: string;
  stapelbar: boolean; gefahrgut: boolean; zollrelevanz: boolean; ladehilfsmitteltausch: boolean;
}

function text(wert: unknown, feld: string, max: number, pflicht = true): string {
  const t = typeof wert === 'string' ? wert.trim() : '';
  if (pflicht && t.length === 0) throw new Eingabefehler(`${feld} fehlt.`);
  if (t.length > max) throw new Eingabefehler(`${feld} darf höchstens ${max} Zeichen haben.`);
  return t;
}

function zahl(wert: unknown, feld: string, min: number, max: number, ganz = false): number {
  const z = typeof wert === 'number' ? wert : Number.NaN;
  if (!Number.isFinite(z) || z < min || z > max || (ganz && !Number.isInteger(z)))
    throw new Eingabefehler(`${feld} muss zwischen ${min} und ${max} liegen.`);
  return z;
}

function auswahl(wert: unknown, feld: string, erlaubt: string[]): string {
  if (typeof wert !== 'string' || !erlaubt.includes(wert)) throw new Eingabefehler(`${feld} ist ungültig.`);
  return wert;
}

function datum(wert: unknown, feld: string): string {
  if (typeof wert !== 'string' || !/^\d{4}-\d{2}-\d{2}$/.test(wert) || Number.isNaN(Date.parse(wert)))
    throw new Eingabefehler(`${feld} ist kein gültiges Datum.`);
  return wert;
}

function uhrzeit(wert: unknown, feld: string): string {
  if (typeof wert !== 'string' || !/^([01]\d|2[0-3]):[0-5]\d$/.test(wert))
    throw new Eingabefehler(`${feld} ist keine gültige Uhrzeit.`);
  return wert;
}

const ja = (wert: unknown) => wert === true;

// Heute in deutscher Zeit als JJJJ-MM-TT
function heute(): string {
  return new Intl.DateTimeFormat('sv-SE', { timeZone: 'Europe/Berlin' }).format(new Date());
}

export function pruefen(b: any): Sendung {
  if (!b || typeof b !== 'object') throw new Eingabefehler('Ungültige Anfrage.');

  const firma = text(b.firma, 'Die Spedition', 40).toLowerCase();
  if (!/^[a-z0-9-]{6,40}$/.test(firma)) throw new Eingabefehler('Bitte eine Spedition auswählen.');

  const a = b.auftraggeber ?? {};
  const email = text(a.email, 'E-Mail', 254).toLowerCase();
  if (!/^[^@\s]+@[^@\s]+\.[^@\s]+$/.test(email)) throw new Eingabefehler('Bitte eine gültige E-Mail-Adresse angeben.');
  const ansprechpartner = text(a.ansprechpartner, 'Ansprechpartner', 150);
  const telefon = text(a.telefon, 'Telefon', 40);

  const ab = b.abholung ?? {};
  const li = b.lieferung ?? {};
  const abStrasse = text(ab.adresse, 'Abholadresse', 200);
  const abPlz = text(ab.plz, 'PLZ der Abholung', 10);
  const abOrt = text(ab.ort, 'Ort der Abholung', 80);
  const liStrasse = text(li.adresse, 'Lieferadresse', 200);
  const liPlz = text(li.plz, 'PLZ der Lieferung', 10);
  const liOrt = text(li.ort, 'Ort der Lieferung', 80);

  const abholdatum = datum(ab.datum, 'Das Abholdatum');
  const lieferdatum = datum(li.datum, 'Das Lieferdatum');
  if (abholdatum < heute()) throw new Eingabefehler('Das Abholdatum darf nicht in der Vergangenheit liegen.');
  if (lieferdatum < abholdatum) throw new Eingabefehler('Das Lieferdatum darf nicht vor dem Abholdatum liegen.');
  const inEinemJahr = new Date(Date.now() + 366 * 86_400_000).toISOString().slice(0, 10);
  if (abholdatum > inEinemJahr) throw new Eingabefehler('Das Abholdatum liegt zu weit in der Zukunft.');

  const abholVon = uhrzeit(ab.von, 'Abholung von');
  const abholBis = uhrzeit(ab.bis, 'Abholung bis');
  const lieferVon = uhrzeit(li.von, 'Lieferung von');
  const lieferBis = uhrzeit(li.bis, 'Lieferung bis');
  if (abholBis < abholVon || lieferBis < lieferVon) throw new Eingabefehler('Bei einem Zeitfenster liegt „bis“ vor „von“.');

  const gutart = auswahl(b.gutart, 'Die Gutart', GUTARTEN);
  const gekuehlt = gutart === 'Frische';
  let temperaturMinC: number | null = null;
  let temperaturMaxC: number | null = null;
  if (gekuehlt) {
    temperaturMinC = zahl(b.temperaturMinC, 'Die Mindesttemperatur (°C)', -30, 30);
    temperaturMaxC = zahl(b.temperaturMaxC, 'Die Höchsttemperatur (°C)', -30, 30);
    if (temperaturMinC > temperaturMaxC) throw new Eingabefehler('Die Mindesttemperatur darf nicht über der Höchsttemperatur liegen.');
  }

  const referenz = text(b.kundenreferenz, 'Ihre Referenz', 100, false);

  return {
    firma,
    kunde: {
      firmenname: text(a.firmenname, 'Firma', 200),
      ansprechpartner, email, telefon,
      // Der Auftraggeber gibt keine eigene Adresse an. Als Kundenadresse nehmen wir die Abholadresse;
      // der Disponent kann sie in der App korrigieren.
      adresse: abStrasse, plz: abPlz, ort: abOrt,
    },
    kundenreferenz: referenz || null,
    abholadresse: `${abStrasse}, ${abPlz} ${abOrt}`,
    abholdatum, abholVon, abholBis,
    lieferadresse: `${liStrasse}, ${liPlz} ${liOrt}`,
    lieferdatum, lieferVon, lieferBis,
    ansprechpartner: `${ansprechpartner}, ${telefon}`,
    avisierung: ja(b.avisierungErforderlich),
    gutart,
    verpackungstyp: auswahl(b.verpackungstyp, 'Die Verpackung', VERPACKUNGEN),
    anzahl: zahl(b.anzahl, 'Die Anzahl', 1, 10_000, true),
    gewichtKg: zahl(b.gewichtKg, 'Das Gewicht (kg)', 0.01, 60_000),
    laengeM: zahl(b.laengeM, 'Die Länge (m)', 0.01, 20),
    breiteM: zahl(b.breiteM, 'Die Breite (m)', 0.01, 3),
    hoeheM: zahl(b.hoeheM, 'Die Höhe (m)', 0.01, 4),
    temperaturMinC, temperaturMaxC,
    equipment: auswahl(b.ladeEntladeequipment ?? 'Keines', 'Das Lade-/Entladeequipment', EQUIPMENT),
    stapelbar: ja(b.stapelbar),
    gefahrgut: ja(b.gefahrgut),
    zollrelevanz: ja(b.zollrelevanz),
    ladehilfsmitteltausch: ja(b.ladehilfsmitteltausch),
  };
}

// ─────────────────────────────────────────────────────────────────────────────
// 2. Speichern. Kunde (falls neu), Sendung und Protokoll in EINEM Befehl: alles oder nichts.
// ─────────────────────────────────────────────────────────────────────────────
// $1 anmelde_code, $2 Kunde (JSON), $3 Sendung (JSON), $4 MAX_PRO_STUNDE
// Kunde: gleiche E-Mail in derselben Firma → vorhandener Kunde, sonst neuer Kunde mit nächster K-Nummer.
// Sendung: nächste A-Nummer (wie im SendungRepository), Status „Angefragt“, Priorität „Normal“.
export const SPEICHERN = `
  WITH firma AS (
      SELECT u.unternehmen_id
      FROM public.unternehmen u
      WHERE u.anmelde_code = $1 AND u.status <> 'Inaktiv'
        AND (SELECT count(*) FROM public.aktionsprotokoll p
             WHERE p.unternehmen_id = u.unternehmen_id AND p.akteur_art = 'Webseite'
               AND p.zeitpunkt > now() - interval '1 hour') < $4::integer
  ), k AS (
      SELECT * FROM jsonb_to_record($2::jsonb) AS x(firmenname text, ansprechpartner text, email text,
                                                     telefon text, adresse text, plz text, ort text)
  ), e AS (
      SELECT * FROM jsonb_to_record($3::jsonb) AS x(
          kundenreferenz text, abholadresse text, abholdatum date, abholfenster_von time, abholfenster_bis time,
          lieferadresse text, lieferdatum date, lieferfenster_von time, lieferfenster_bis time,
          ansprechpartner text, avisierung_erforderlich boolean, gutart text, verpackungstyp text, anzahl integer,
          gewicht_kg numeric, laenge_m numeric, breite_m numeric, hoehe_m numeric,
          temperatur_min_c numeric, temperatur_max_c numeric, stapelbar boolean, gefahrgut boolean,
          ladehilfsmitteltausch boolean, zollrelevanz boolean, lade_entladeequipment text)
  ), vorhanden AS (
      SELECT kd.kunde_id
      FROM public.kunde kd, firma f, k
      WHERE kd.unternehmen_id = f.unternehmen_id AND lower(btrim(kd.email)) = k.email
      ORDER BY kd.kunde_id
      LIMIT 1
  ), neuer_kunde AS (
      INSERT INTO public.kunde (kundennummer, firmenname, ansprechpartner, adresse, plz, ort, telefon, email, unternehmen_id)
      SELECT 'K-' || (COALESCE((SELECT max(substring(kundennummer FROM '^K-([0-9]{1,9})$')::bigint)
                                FROM public.kunde WHERE unternehmen_id = f.unternehmen_id), 1000) + 1),
             k.firmenname, k.ansprechpartner, k.adresse, k.plz, k.ort, k.telefon, k.email, f.unternehmen_id
      FROM firma f, k
      WHERE NOT EXISTS (SELECT 1 FROM vorhanden)
      RETURNING kunde_id
  ), kunde_wahl AS (
      SELECT kunde_id FROM vorhanden
      UNION ALL
      SELECT kunde_id FROM neuer_kunde
  ), angelegt AS (
      INSERT INTO public.auftrag (auftragsnummer, kunde_id, kundenreferenz, abholadresse, lieferadresse,
          abholdatum, lieferdatum, abholfenster_von, abholfenster_bis, lieferfenster_von, lieferfenster_bis,
          gewicht_kg, laenge_m, breite_m, hoehe_m, verpackungstyp, anzahl, gutart, temperatur_min_c, temperatur_max_c,
          stapelbar, drehbar, gefahrgut, warenwert, versicherung, ansprechpartner, avisierung_erforderlich,
          ladehilfsmitteltausch, lade_entladeequipment, zollrelevanz, status, prioritaet, unternehmen_id)
      SELECT 'A-' || (COALESCE((SELECT max(substring(auftragsnummer FROM '^A-([0-9]{1,9})$')::bigint)
                                FROM public.auftrag WHERE unternehmen_id = f.unternehmen_id), 1000) + 1),
          kw.kunde_id, e.kundenreferenz, e.abholadresse, e.lieferadresse,
          e.abholdatum, e.lieferdatum, e.abholfenster_von, e.abholfenster_bis, e.lieferfenster_von, e.lieferfenster_bis,
          e.gewicht_kg, e.laenge_m, e.breite_m, e.hoehe_m, e.verpackungstyp, e.anzahl, e.gutart, e.temperatur_min_c, e.temperatur_max_c,
          e.stapelbar, false, e.gefahrgut, 0, false, e.ansprechpartner, e.avisierung_erforderlich,
          e.ladehilfsmitteltausch, e.lade_entladeequipment, e.zollrelevanz, 'Angefragt', 'Normal', f.unternehmen_id
      FROM firma f, e, kunde_wahl kw
      RETURNING auftrag_id, auftragsnummer, unternehmen_id
  ), protokoll AS (
      INSERT INTO public.aktionsprotokoll (akteur_art, aktion, ergebnis, detail, unternehmen_id, abteilung_id, benutzer_id)
      SELECT 'Webseite', 'Sendung angefragt', 'Gespeichert', 'Sendung ' || a.auftragsnummer || ' über die Webseite',
             a.unternehmen_id, NULL, NULL
      FROM angelegt a
  )
  SELECT auftragsnummer FROM angelegt`;

// Warum wurde nichts gespeichert? Firma unbekannt/gesperrt oder Stundengrenze erreicht.
export const FIRMA_PRUEFEN = `SELECT status FROM public.unternehmen WHERE anmelde_code = $1`;

// Für das Auswahlfeld: alle nicht gesperrten Firmen. Nur Name und Code, keine weiteren Daten.
export const FIRMEN_LISTE = `
  SELECT anmelde_code AS code, firmenname AS name
  FROM public.unternehmen
  WHERE status <> 'Inaktiv'
  ORDER BY lower(firmenname)`;

export type Abfrage = (query: string, params: unknown[]) => Promise<Record<string, any>[]>;

export async function speichern(s: Sendung, abfrage: Abfrage): Promise<
  { ok: true; auftragsnummer: string } | { ok: false; status: number; fehler: string }> {
  const kunde = JSON.stringify(s.kunde);
  const sendung = JSON.stringify({
    kundenreferenz: s.kundenreferenz,
    abholadresse: s.abholadresse, abholdatum: s.abholdatum, abholfenster_von: s.abholVon, abholfenster_bis: s.abholBis,
    lieferadresse: s.lieferadresse, lieferdatum: s.lieferdatum, lieferfenster_von: s.lieferVon, lieferfenster_bis: s.lieferBis,
    ansprechpartner: s.ansprechpartner, avisierung_erforderlich: s.avisierung,
    gutart: s.gutart, verpackungstyp: s.verpackungstyp, anzahl: s.anzahl, gewicht_kg: s.gewichtKg,
    laenge_m: s.laengeM, breite_m: s.breiteM, hoehe_m: s.hoeheM,
    temperatur_min_c: s.temperaturMinC, temperatur_max_c: s.temperaturMaxC,
    stapelbar: s.stapelbar, gefahrgut: s.gefahrgut, ladehilfsmitteltausch: s.ladehilfsmitteltausch,
    zollrelevanz: s.zollrelevanz, lade_entladeequipment: s.equipment,
  });

  // Zwei Anfragen gleichzeitig können dieselbe A-/K-Nummer berechnen. Die Datenbank lehnt die zweite
  // dann ab (eindeutige Nummer je Firma, Fehler 23505) – wir versuchen es einfach noch einmal.
  for (let versuch = 1; ; versuch++) {
    try {
      const rows = await abfrage(SPEICHERN, [s.firma, kunde, sendung, MAX_PRO_STUNDE]);
      if (rows.length === 1) return { ok: true, auftragsnummer: rows[0].auftragsnummer };
      break;
    } catch (fehler: any) {
      if (fehler?.code === '23505' && versuch < 3) continue;
      throw fehler;
    }
  }

  const firma = await abfrage(FIRMA_PRUEFEN, [s.firma]);
  if (firma.length === 0 || firma[0].status === 'Inaktiv')
    return { ok: false, status: 404, fehler: 'Diese Spedition nimmt gerade keine Online-Anmeldungen an. Bitte laden Sie die Seite neu.' };
  return { ok: false, status: 429, fehler: 'Gerade kommen sehr viele Anmeldungen an. Bitte versuchen Sie es in einer Stunde noch einmal.' };
}

// ─────────────────────────────────────────────────────────────────────────────
// 3. HTTP: CORS, Größe begrenzen, Antworten
// ─────────────────────────────────────────────────────────────────────────────
const CORS = {
  'access-control-allow-origin': '*',
  'access-control-allow-methods': 'GET, POST, OPTIONS',
  'access-control-allow-headers': 'content-type',
  'access-control-max-age': '86400',
};

export async function bearbeiten(request: Request, abfrage: Abfrage): Promise<Response> {
  const antwort = (status: number, body: unknown) => Response.json(body, { status, headers: CORS });

  if (request.method === 'OPTIONS') return new Response(null, { status: 204, headers: CORS });

  if (request.method === 'GET') {
    try {
      return antwort(200, { firmen: await abfrage(FIRMEN_LISTE, []) });
    } catch (fehler) {
      console.error('sendung-funktion (Liste):', (fehler as Error)?.message);
      return antwort(500, { fehler: 'Die Speditionen konnten nicht geladen werden.' });
    }
  }

  if (request.method !== 'POST') return antwort(405, { fehler: 'Nur GET und POST.' });

  const roh = await request.text();
  if (roh.length > MAX_BODY_BYTES) return antwort(413, { fehler: 'Die Anfrage ist zu groß.' });

  let body: unknown;
  try { body = JSON.parse(roh); } catch { return antwort(400, { fehler: 'Ungültige Anfrage.' }); }

  try {
    const sendung = pruefen(body);
    const ergebnis = await speichern(sendung, abfrage);
    return ergebnis.ok
      ? antwort(201, { auftragsnummer: ergebnis.auftragsnummer })
      : antwort(ergebnis.status, { fehler: ergebnis.fehler });
  } catch (fehler) {
    if (fehler instanceof Eingabefehler) return antwort(400, { fehler: fehler.message });
    // Ohne Kundendaten loggen (Datenschutz).
    console.error('sendung-funktion:', (fehler as Error)?.message);
    return antwort(500, { fehler: 'Die Sendung konnte gerade nicht gespeichert werden. Bitte später erneut versuchen.' });
  }
}

const sql = neon(process.env.DATABASE_URL!);
const neonAbfrage: Abfrage = (query, params) => sql.query(query, params) as Promise<Record<string, any>[]>;

export default {
  fetch: (request: Request) => bearbeiten(request, neonAbfrage),
};
