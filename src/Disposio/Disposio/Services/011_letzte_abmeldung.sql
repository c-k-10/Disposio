-- Einmalige additive Migration für D01 „Tagesübersicht“: Zeitpunkt der letzten Abmeldung je Konto.
-- Das Programm führt diese Schemaänderung NICHT beim Start aus. Bitte einmal im Neon SQL Editor ausführen.
-- Die App setzt den Wert beim Klick auf „Abmelden“ und beim Schließen des Fensters (TagesuebersichtRepository).
-- D01 zeigt unter „Seit Ihrer letzten Schicht“ alles ab diesem Zeitpunkt (höchstens 3 Tage zurück).
BEGIN;

ALTER TABLE public.benutzer
    ADD COLUMN IF NOT EXISTS letzte_abmeldung timestamptz;

COMMENT ON COLUMN public.benutzer.letzte_abmeldung IS
    'Letzte Abmeldung (Abmelden-Knopf oder Fenster geschlossen). NULL = noch nie abgemeldet. Für D01 „Seit Ihrer letzten Schicht“.';

COMMIT;
