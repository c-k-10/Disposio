-- Einmalige Migration: Ein Mandant (Unternehmen) kann im Admin-Dashboard gesperrt werden.
-- Das Programm führt diese Schemaänderung NICHT beim Start aus.
-- Der Status bekommt den dritten Wert „Inaktiv“. Vorhandene Unternehmen behalten ihren Status.
-- Bei einer inaktiven Firma kommen nur Inhaber/Geschäftsführer und Kundenadmin noch in die
-- Monatsstatistik; alle anderen Konten können sich nicht anmelden. Die Daten bleiben erhalten.
BEGIN;

ALTER TABLE public.unternehmen DROP CONSTRAINT IF EXISTS unternehmen_dashboard_status_ck;
ALTER TABLE public.unternehmen ADD CONSTRAINT unternehmen_dashboard_status_ck
    CHECK (status IN ('Aktiv', 'Testphase', 'Inaktiv'));

COMMENT ON COLUMN public.unternehmen.status IS
    'Testphase, Aktiv oder Inaktiv. Inaktiv = gesperrt: nur Inhaber/Geschäftsführer und Kundenadmin sehen noch die Statistik, alle anderen Konten sind gesperrt. Daten bleiben erhalten.';

COMMIT;
