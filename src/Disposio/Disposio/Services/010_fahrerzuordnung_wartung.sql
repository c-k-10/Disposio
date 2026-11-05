-- Einmalige additive Migration für US-03 „Fahrer einem Fahrzeug zuweisen“ (#83–#88) und
-- US-05 „Wartungstermine und technischen Status überwachen“ (#89–#92).
-- Das Programm führt diese Schemaänderung NICHT beim Start aus. Erst prüfen, dann in Neon ausführen.
-- Alle Schritte sind wiederholbar: Was schon da ist, bleibt unverändert.
--
-- Die Tabellen public.fahrer_fahrzeug_zuordnung und public.wartung gibt es schon. Hier kommen nur
-- die Regeln dazu, die die Oberfläche (F03, F04, F05) braucht.
BEGIN;

-- NEXT COMMAND
-- btree_gist erlaubt „gleiches Fahrzeug UND überlappender Zeitraum“ in EINER Ausschlussregel.
CREATE EXTENSION IF NOT EXISTS btree_gist;

-- NEXT COMMAND
-- #87 Doppelbelegungen verhindern: Ein Fahrzeug hat zu jedem Zeitpunkt höchstens eine Fahrerzuordnung.
-- gueltig_bis = NULL heißt „läuft noch“ (offenes Ende). Die Zeiträume sind [von, bis): Endet eine
-- Zuordnung um 10:00, darf die nächste um 10:00 beginnen.
-- DEFERRABLE INITIALLY DEFERRED: Beim „Fahrer wechseln“ wird die alte Zuordnung beendet und die neue
-- im selben Befehl angelegt. Geprüft wird deshalb erst am Ende des Befehls, nicht mittendrin.
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint
        WHERE conname = 'fahrer_fahrzeug_zuordnung_keine_doppelbelegung'
          AND conrelid = 'public.fahrer_fahrzeug_zuordnung'::regclass) THEN
        ALTER TABLE public.fahrer_fahrzeug_zuordnung
            ADD CONSTRAINT fahrer_fahrzeug_zuordnung_keine_doppelbelegung
            EXCLUDE USING gist (fahrzeug_id WITH =, tstzrange(gueltig_von, gueltig_bis, '[)') WITH &&)
            DEFERRABLE INITIALLY DEFERRED;
    END IF;
END $$;

-- NEXT COMMAND
COMMENT ON TABLE public.fahrer_fahrzeug_zuordnung IS
    'Welcher Fahrer welches Fahrzeug nutzt (F03). gueltig_bis NULL = aktuelle Zuordnung. Beendete Zuordnungen bleiben als Historie erhalten.';

-- NEXT COMMAND
-- US-05: Eine Zeile in public.wartung ist entweder ein geplanter Termin oder eine durchgeführte Wartung.
--   Geplant:  datum = Termin, faellig_bei_kilometerstand optional, naechster_termin leer.
--   Erledigt: datum = Tag der Durchführung, kilometerstand_bei_durchfuehrung, kosten, beschreibung = Bemerkung,
--             naechster_termin = Folgetermin (falls gleich einer vereinbart wurde).
-- Für einen geplanten Termin gibt es noch keinen „nächsten Termin“, deshalb darf die Spalte leer sein.
ALTER TABLE public.wartung ALTER COLUMN naechster_termin DROP NOT NULL;

-- NEXT COMMAND
ALTER TABLE public.wartung ADD COLUMN IF NOT EXISTS kilometerstand_bei_durchfuehrung integer;

-- NEXT COMMAND
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint
        WHERE conname = 'wartung_kilometerstand_ck' AND conrelid = 'public.wartung'::regclass) THEN
        ALTER TABLE public.wartung ADD CONSTRAINT wartung_kilometerstand_ck
            CHECK (kilometerstand_bei_durchfuehrung IS NULL OR kilometerstand_bei_durchfuehrung >= 0);
    END IF;

    -- Nur diese zwei Zustände. Eine erledigte Wartung braucht ihren Kilometerstand (#91 Historie).
    IF NOT EXISTS (SELECT 1 FROM pg_constraint
        WHERE conname = 'wartung_status_ck' AND conrelid = 'public.wartung'::regclass) THEN
        ALTER TABLE public.wartung ADD CONSTRAINT wartung_status_ck
            CHECK (status IN ('Geplant', 'Erledigt')
                   AND (status = 'Geplant' OR kilometerstand_bei_durchfuehrung IS NOT NULL));
    END IF;

    IF NOT EXISTS (SELECT 1 FROM pg_constraint
        WHERE conname = 'wartung_faellig_km_ck' AND conrelid = 'public.wartung'::regclass) THEN
        ALTER TABLE public.wartung ADD CONSTRAINT wartung_faellig_km_ck
            CHECK (faellig_bei_kilometerstand IS NULL OR faellig_bei_kilometerstand >= 0);
    END IF;
END $$;

-- NEXT COMMAND
-- F01 und F04 suchen je Fahrzeug die offenen Termine.
CREATE INDEX IF NOT EXISTS wartung_geplant_idx ON public.wartung (fahrzeug_id, datum) WHERE status = 'Geplant';

-- NEXT COMMAND
COMMENT ON COLUMN public.wartung.status IS
    'Geplant = offener Termin (datum = Termin), Erledigt = durchgeführt (datum = Durchführung). Nur Erledigt zählt in der Statistik.';

-- NEXT COMMAND
COMMENT ON COLUMN public.wartung.kilometerstand_bei_durchfuehrung IS
    'Kilometerstand am Tag der Durchführung. Pflicht bei Erledigt.';

-- NEXT COMMAND
COMMIT;
