-- Einmalige additive Migration: Fahrzeuge werden nicht gelöscht, sondern inaktiv gesetzt (Issue #76).
-- Das Programm führt diese Schemaänderung NICHT beim Start aus.
-- Alle vorhandenen Fahrzeuge bleiben aktiv. Touren, Wartungen, Tankvorgänge und Dokumente
-- verweisen weiter auf das Fahrzeug, auch wenn es inaktiv ist.
BEGIN;

ALTER TABLE public.fahrzeug
    ADD COLUMN IF NOT EXISTS aktiv boolean NOT NULL DEFAULT true;

COMMENT ON COLUMN public.fahrzeug.aktiv IS
    'false = ausgemustert: bleibt mit seinem Verlauf erhalten, wird aber nicht mehr eingeplant. Getrennt von status (Verfügbar/Wartung/Gesperrt).';

COMMIT;
