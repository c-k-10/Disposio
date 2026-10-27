-- Einmalige additive Migration: Abteilungen werden nicht gelöscht, sondern deaktiviert (K05).
-- Das Programm führt diese Schemaänderung NICHT beim Start aus. Bitte vor dem Start der neuen Version
-- im Neon SQL Editor ausführen, sonst schlagen die Abteilungs-Abfragen fehl.
-- Benutzer, Fahrzeuge, Touren und das Aktionsprotokoll verweisen weiter auf die Abteilung,
-- auch wenn sie inaktiv ist. Deaktivierte Abteilungen werden für neue Touren nicht mehr angeboten.
BEGIN;

ALTER TABLE public.abteilung
    ADD COLUMN IF NOT EXISTS aktiv boolean NOT NULL DEFAULT true;

COMMENT ON COLUMN public.abteilung.aktiv IS
    'false = deaktiviert: bleibt mit allen Verweisen erhalten, wird aber für neue Touren nicht mehr angeboten.';

-- Jede Firma braucht mindestens eine Abteilung, weil tour.abteilung_id Pflicht ist.
-- Firmen ohne Abteilung bekommen deshalb einmalig eine „Hauptabteilung“.
INSERT INTO public.abteilung (bezeichnung, unternehmen_id)
SELECT 'Hauptabteilung', u.unternehmen_id
FROM public.unternehmen u
WHERE NOT EXISTS (SELECT 1 FROM public.abteilung a WHERE a.unternehmen_id = u.unternehmen_id);

COMMIT;
