// Neon Function: legt Neon-Auth-Konten an (und entfernt sie bei Bedarf wieder).
//
// Wer darf aufrufen: nur angemeldete Nutzer, die in der Tabelle "benutzer" aktiv sind und entweder
//   - Kundenadmin einer nicht gesperrten Firma sind (Standardrolle, unternehmen_id der Rolle ist NULL) oder
//   - Plattform-Admin (SuperKrasserAdmin) ohne Firma.
// Die Funktion prüft das JWT von Neon Auth und danach die Rolle in der Datenbank. Sie vertraut
// nie Angaben der App über Rolle oder Firma.
//
// Geheimnisse (nur beim Deploy mit --env setzen, nie in den Code schreiben):
//   DIENST_ADMIN_EMAIL, DIENST_ADMIN_PASSWORT  -> Konto mit Admin-Rolle in Neon Auth,
//                                                  das NICHT in der Tabelle "benutzer" steht.
// Von Neon automatisch gesetzt: DATABASE_URL, NEON_AUTH_BASE_URL, NEON_AUTH_JWKS_URL.

import { createRemoteJWKSet, jwtVerify } from 'jose';
import { neon } from '@neondatabase/serverless';

const AUTH_BASE = process.env.NEON_AUTH_BASE_URL!.replace(/\/$/, '');
const jwks = createRemoteJWKSet(new URL(process.env.NEON_AUTH_JWKS_URL!));
const issuer = new URL(AUTH_BASE).origin;
const sql = neon(process.env.DATABASE_URL!);

// Neon Auth prüft den Origin. localhost ist laut Neon-Doku immer erlaubt.
const ORIGIN = process.env.AUTH_ORIGIN ?? 'http://localhost:3000';

const antwort = (status: number, body: unknown) => Response.json(body, { status });

// 1. Wer ruft auf? JWT prüfen und die Nutzer-ID (sub) lesen.
async function aufrufer(request: Request): Promise<string | null> {
  const header = request.headers.get('authorization');
  if (!header?.toLowerCase().startsWith('bearer ')) return null;
  try {
    const { payload } = await jwtVerify(header.slice(7), jwks, { issuer });
    return typeof payload.sub === 'string' ? payload.sub : null;
  } catch {
    return null;
  }
}

// 2. Darf diese Person Konten anlegen? Die Antwort kommt aus der Datenbank, nicht aus der App.
//    Ein Kundenadmin einer gesperrten Firma (unternehmen.status = 'Inaktiv') darf es nicht.
async function darfKontenAnlegen(authId: string): Promise<boolean> {
  const rows = await sql`
    SELECT 1 FROM public.benutzer b
    WHERE b.auth_user_id = ${authId} AND b.aktiv = true
      AND (b.unternehmen_id IS NULL OR EXISTS (
        SELECT 1 FROM public.unternehmen u
        WHERE u.unternehmen_id = b.unternehmen_id AND u.status <> 'Inaktiv'))
      AND EXISTS (
        SELECT 1 FROM public.benutzer_rolle br
        JOIN public.rolle r ON r.rollen_id = br.rollen_id
        WHERE br.benutzer_id = b.benutzer_id AND r.unternehmen_id IS NULL
          AND ((b.unternehmen_id IS NOT NULL AND r.bezeichnung = 'Kundenadmin')
            OR (b.unternehmen_id IS NULL AND r.bezeichnung = 'SuperKrasserAdmin')))
    LIMIT 1`;
  return rows.length === 1;
}

// 3. Die Funktion meldet sich mit dem Dienst-Admin bei Neon Auth an (Cookie-Sitzung).
async function dienstAdmin(): Promise<{ cookie: string; id: string }> {
  const res = await fetch(`${AUTH_BASE}/sign-in/email`, {
    method: 'POST',
    headers: { 'content-type': 'application/json', origin: ORIGIN },
    body: JSON.stringify({
      email: process.env.DIENST_ADMIN_EMAIL,
      password: process.env.DIENST_ADMIN_PASSWORT,
    }),
  });
  if (!res.ok) throw new Error('Dienst-Admin-Anmeldung fehlgeschlagen');
  const cookie = res.headers
    .getSetCookie()
    .map((c) => c.split(';')[0])
    .join('; ');
  const data: any = await res.json();
  return { cookie, id: data.user.id };
}

async function anlegen(b: any): Promise<Response> {
  const email = String(b.email ?? '').trim().toLowerCase();
  const name = String(b.name ?? '').trim();
  const passwort = String(b.passwort ?? '');
  if (email.length > 254 || !/^[^@\s]+@[^@\s]+\.[^@\s]+$/.test(email))
    return antwort(400, { fehler: 'Ungültige E-Mail-Adresse.' });
  if (!name || name.length > 200) return antwort(400, { fehler: 'Name fehlt oder ist zu lang.' });
  if (passwort.length < 8 || passwort.length > 128)
    return antwort(400, { fehler: 'Das Passwort muss 8 bis 128 Zeichen lang sein.' });

  const dienst = await dienstAdmin();
  const res = await fetch(`${AUTH_BASE}/admin/create-user`, {
    method: 'POST',
    headers: { 'content-type': 'application/json', origin: ORIGIN, cookie: dienst.cookie },
    // role "user": Das neue Konto bekommt in Neon Auth keine Admin-Rechte.
    body: JSON.stringify({ email, password: passwort, name, role: 'user' }),
  });
  const data: any = await res.json().catch(() => ({}));
  if (!res.ok || !data?.user?.id) {
    return antwort(res.status >= 500 ? 502 : 400, {
      fehler: data?.message ?? 'Das Konto konnte nicht angelegt werden.',
    });
  }
  return antwort(200, { authId: data.user.id });
}

// Entfernt nur Konten, die in "benutzer" nicht vorkommen (übrig gebliebene Reste nach einem
// fehlgeschlagenen Speichern). Bestehende Konten und der Dienst-Admin sind geschützt.
async function entfernen(b: any): Promise<Response> {
  const authId = String(b.authId ?? '');
  if (!authId) return antwort(400, { fehler: 'authId fehlt.' });

  const benutzt = await sql`SELECT 1 FROM public.benutzer WHERE auth_user_id = ${authId} LIMIT 1`;
  if (benutzt.length > 0) return antwort(409, { fehler: 'Das Konto wird verwendet.' });

  const dienst = await dienstAdmin();
  if (authId === dienst.id) return antwort(403, { fehler: 'Nicht erlaubt.' });

  const res = await fetch(`${AUTH_BASE}/admin/remove-user`, {
    method: 'POST',
    headers: { 'content-type': 'application/json', origin: ORIGIN, cookie: dienst.cookie },
    body: JSON.stringify({ userId: authId }),
  });
  return res.ok ? antwort(200, { ok: true }) : antwort(502, { fehler: 'Entfernen fehlgeschlagen.' });
}

export default {
  async fetch(request: Request): Promise<Response> {
    if (request.method !== 'POST') return antwort(405, { fehler: 'Nur POST.' });

    const authId = await aufrufer(request);
    if (!authId) return antwort(401, { fehler: 'Nicht angemeldet.' });

    try {
      if (!(await darfKontenAnlegen(authId))) return antwort(403, { fehler: 'Keine Berechtigung.' });

      const body: any = await request.json().catch(() => null);
      if (body?.aktion === 'anlegen') return await anlegen(body);
      if (body?.aktion === 'entfernen') return await entfernen(body);
      return antwort(400, { fehler: 'Unbekannte Aktion.' });
    } catch (fehler) {
      // Absichtlich ohne Request-Inhalt loggen: Das Startpasswort darf nirgends auftauchen.
      console.error('konto-funktion:', (fehler as Error)?.message);
      return antwort(502, { fehler: 'Interner Fehler.' });
    }
  },
};
