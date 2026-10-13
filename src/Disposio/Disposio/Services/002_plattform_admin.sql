-- Einmalige Ergänzung für die Projektbesitzer: Sie gehören zu keiner Kundenfirma.
-- NULL bedeutet hier ausdrücklich „kein Unternehmen“, nicht eine neue Beispiel-Firma.
-- Der vorhandene Fremdschlüssel bleibt bestehen: Firmenkonten müssen weiterhin auf
-- ein wirklich vorhandenes Unternehmen verweisen.
BEGIN;

-- NEXT COMMAND
ALTER TABLE public.benutzer ALTER COLUMN unternehmen_id DROP NOT NULL;

-- NEXT COMMAND
-- Ohne Firma kann das Konto auch keiner Firmenabteilung angehören.
-- Die Abfrage macht den Schritt wiederholbar: Eine vorhandene Regel bleibt erhalten.
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint
        WHERE conname = 'benutzer_plattform_ohne_abteilung_ck'
          AND conrelid = 'public.benutzer'::regclass) THEN
        ALTER TABLE public.benutzer ADD CONSTRAINT benutzer_plattform_ohne_abteilung_ck
            CHECK (unternehmen_id IS NOT NULL OR abteilung_id IS NULL);
    END IF;
END $$;

-- NEXT COMMAND
-- Der bestehende eindeutige Schlüssel (email, unternehmen_id) schützt Firmenkonten.
-- Bei NULL braucht PostgreSQL einen zusätzlichen Index für die Plattformkonten.
-- So können Admin@Disposio.com und admin@disposio.com nicht zwei Plattformkonten sein.
CREATE UNIQUE INDEX IF NOT EXISTS ux_benutzer_plattform_email
    ON public.benutzer (lower(btrim(email)))
    WHERE unternehmen_id IS NULL;

-- NEXT COMMAND
-- Dies ist die schon vorhandene Rolle. Wir erfinden keine zweite Schreibweise.
INSERT INTO public.rolle (bezeichnung) VALUES ('SuperKrasserAdmin')
    ON CONFLICT (bezeichnung) DO NOTHING;

-- NEXT COMMAND
COMMENT ON COLUMN public.benutzer.unternehmen_id IS
    'Firmenkonto: Unternehmen-ID; Plattformkonto des Projektbesitzers: NULL. Dashboard-Zugang erfordert zusätzlich SuperKrasserAdmin.';

-- NEXT COMMAND
COMMIT;
