-- Einmalige additive Migration: Sendungen über die Webseite anmelden (Disposio.web + neon-function/sendung.ts).
-- Das Programm führt diese Schemaänderung NICHT beim Start aus. Bitte einmal im Neon SQL Editor ausführen.
--
-- 1. Jede Firma bekommt einen zufälligen Anmeldecode für ihren Link: …/anmelden.html?firma=<anmelde_code>
--    Zufällig statt Firmenname, damit niemand fremde Links erraten kann.
-- 2. Sendungen von der Webseite bekommen den Status „Angefragt“. Der Disponent sieht so in D02,
--    dass die Sendung von außen kommt und noch geprüft werden muss.
-- 3. Im Aktionsprotokoll steht bei solchen Sendungen die Akteur-Art „Webseite“.
--
-- Falls es auf auftrag.status oder aktionsprotokoll.akteur_art eine Werteliste (CHECK) gibt, wird der neue
-- Wert dort ergänzt. Gibt es keine Werteliste, ändert sich an diesen Spalten nichts.
BEGIN;

-- ── 1. Anmeldecode je Firma ──────────────────────────────────────────────────────────────
ALTER TABLE public.unternehmen
    ADD COLUMN IF NOT EXISTS anmelde_code text;

-- 12 zufällige Hex-Zeichen (aus gen_random_uuid, in PostgreSQL ab Version 13 eingebaut).
UPDATE public.unternehmen
SET anmelde_code = left(replace(gen_random_uuid()::text, '-', ''), 12)
WHERE anmelde_code IS NULL;

ALTER TABLE public.unternehmen
    ALTER COLUMN anmelde_code SET DEFAULT left(replace(gen_random_uuid()::text, '-', ''), 12),
    ALTER COLUMN anmelde_code SET NOT NULL;

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'unternehmen_anmelde_code_ck') THEN
        ALTER TABLE public.unternehmen ADD CONSTRAINT unternehmen_anmelde_code_ck
            CHECK (anmelde_code ~ '^[a-z0-9-]{6,40}$');
    END IF;
END $$;

CREATE UNIQUE INDEX IF NOT EXISTS ux_unternehmen_anmelde_code ON public.unternehmen (anmelde_code);

COMMENT ON COLUMN public.unternehmen.anmelde_code IS
    'Code im Link der Webseite (anmelden.html?firma=…). Neu würfeln, wenn ein Link nicht mehr gelten soll.';

-- ── 2./3. Neue Werte in vorhandenen Wertelisten ergänzen ──────────────────────────────────
-- Sucht CHECK-Bedingungen der Form  spalte = ANY (ARRAY['A'::text, 'B'::text])  bzw.  spalte IN ('A', 'B')
-- und fügt den neuen Wert vorne in die Liste ein.
DO $$
DECLARE
    ziel record;
    c record;
    def text;
    neu text;
BEGIN
    FOR ziel IN
        SELECT * FROM (VALUES ('auftrag', 'status', 'Angefragt'),
                              ('aktionsprotokoll', 'akteur_art', 'Webseite')) AS t(tabelle, spalte, wert)
    LOOP
        FOR c IN
            SELECT con.conname, pg_get_constraintdef(con.oid) AS def
            FROM pg_constraint con
            JOIN pg_class cl ON cl.oid = con.conrelid
            JOIN pg_namespace ns ON ns.oid = cl.relnamespace
            JOIN pg_attribute att ON att.attrelid = con.conrelid AND att.attnum = ANY (con.conkey)
            WHERE ns.nspname = 'public' AND cl.relname = ziel.tabelle
              AND con.contype = 'c' AND att.attname = ziel.spalte
        LOOP
            def := c.def;
            CONTINUE WHEN position(quote_literal(ziel.wert) IN def) > 0;   -- Wert ist schon erlaubt

            IF position('ARRAY[' IN def) > 0 THEN
                neu := regexp_replace(def, 'ARRAY\[', 'ARRAY[' || quote_literal(ziel.wert) || '::text, ');
            ELSE
                RAISE EXCEPTION 'Die CHECK-Bedingung % auf %.% hat ein unbekanntes Format: %. Bitte den Wert % von Hand ergänzen.',
                    c.conname, ziel.tabelle, ziel.spalte, def, ziel.wert;
            END IF;

            EXECUTE format('ALTER TABLE public.%I DROP CONSTRAINT %I', ziel.tabelle, c.conname);
            EXECUTE format('ALTER TABLE public.%I ADD CONSTRAINT %I %s', ziel.tabelle, c.conname, neu);
            RAISE NOTICE 'Wert % in %.% ergänzt (%).', ziel.wert, ziel.tabelle, ziel.spalte, c.conname;
        END LOOP;
    END LOOP;
END $$;

COMMIT;

-- Danach: Codes der Firmen ansehen, um die Links zu bauen.
-- SELECT firmenname, kennung, anmelde_code FROM public.unternehmen ORDER BY firmenname;
