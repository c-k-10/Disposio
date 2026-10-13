-- Einmalige additive Migration für die vorhandene Unternehmen-Tabelle.
-- Das Programm führt diese Schemaänderung NICHT beim Start aus.
-- Adress-/Kontaktdaten, Passwortregeln und vorhandene Benutzer bleiben erhalten.
BEGIN;

ALTER TABLE public.unternehmen
    ADD COLUMN IF NOT EXISTS kennung text
        CONSTRAINT unternehmen_dashboard_kennung_ck CHECK (btrim(kennung) <> '' AND length(kennung) <= 100),
    ADD COLUMN IF NOT EXISTS admin_email text
        CONSTRAINT unternehmen_dashboard_admin_email_ck CHECK (btrim(admin_email) <> '' AND length(admin_email) <= 254),
    ADD COLUMN IF NOT EXISTS status text DEFAULT 'Testphase'
        CONSTRAINT unternehmen_dashboard_status_ck CHECK (status IN ('Aktiv', 'Testphase')),
    ADD COLUMN IF NOT EXISTS updated_at timestamptz DEFAULT clock_timestamp();

-- Nur bisher fehlende neue Felder ergänzen. Bereits vorhandene Werte werden nicht überschrieben.
UPDATE public.unternehmen
SET kennung = COALESCE(kennung, 'unternehmen-' || unternehmen_id),
    admin_email = COALESCE(admin_email, email),
    status = COALESCE(status, 'Testphase'),
    updated_at = COALESCE(updated_at, clock_timestamp())
WHERE kennung IS NULL OR admin_email IS NULL OR status IS NULL OR updated_at IS NULL;

ALTER TABLE public.unternehmen
    ALTER COLUMN kennung SET NOT NULL,
    ALTER COLUMN admin_email SET NOT NULL,
    ALTER COLUMN status SET NOT NULL,
    ALTER COLUMN status SET DEFAULT 'Testphase',
    ALTER COLUMN updated_at SET NOT NULL,
    ALTER COLUMN updated_at SET DEFAULT clock_timestamp();

-- „Firma1“, „firma1“ und „ firma1 “ gelten als dieselbe Anmeldekennung.
-- Die Datenbank prüft das auch bei zwei gleichzeitigen Speichervorgängen.
CREATE UNIQUE INDEX IF NOT EXISTS ux_unternehmen_kennung
    ON public.unternehmen (lower(btrim(kennung)));

COMMIT;
