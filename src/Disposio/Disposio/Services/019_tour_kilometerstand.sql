-- Einmalige additive Migration: Start- und End-Kilometerstand je Tour (US-04, #171–#174).
-- Das Programm führt diese Schemaänderung NICHT beim Start aus. Bitte einmal im Neon SQL Editor ausführen.
--
-- Die Fahrer-App fragt den Kilometerstand bei „Fahrt starten“ und bei „Tour beenden“ ab. Die Fahrer-API
-- (Disposio.Api) speichert ihn hier und setzt den Kilometerstand des Fahrzeugs hoch, wenn er größer ist.
-- Gefahrene Strecke = end_kilometerstand − start_kilometerstand.
-- Ohne diese Migration läuft die API weiter, speichert die Werte an der Tour aber nicht.
BEGIN;

ALTER TABLE public.tour
    ADD COLUMN IF NOT EXISTS start_kilometerstand integer,
    ADD COLUMN IF NOT EXISTS end_kilometerstand integer;

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint
                   WHERE conname = 'tour_kilometerstand_ck' AND conrelid = 'public.tour'::regclass) THEN
        ALTER TABLE public.tour
            ADD CONSTRAINT tour_kilometerstand_ck CHECK (
                (start_kilometerstand IS NULL OR start_kilometerstand >= 0)
                AND (end_kilometerstand IS NULL OR end_kilometerstand >= 0)
                AND (start_kilometerstand IS NULL OR end_kilometerstand IS NULL
                     OR end_kilometerstand >= start_kilometerstand));
    END IF;
END $$;

COMMENT ON COLUMN public.tour.start_kilometerstand IS
    'Kilometerstand bei Abfahrt (Fahrer-App, „Fahrt starten“). Leer = nicht erfasst.';
COMMENT ON COLUMN public.tour.end_kilometerstand IS
    'Kilometerstand bei Tourende (Fahrer-App, „Tour beenden“). Nie kleiner als der Start-Kilometerstand.';

COMMIT;
