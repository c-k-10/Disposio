-- Einmalige additive Migration: Darstellung (hell/dunkel und Akzentfarbe) je Konto.
-- Das Programm führt diese Schemaänderung NICHT beim Start aus. Bitte einmal im Neon SQL Editor ausführen.
-- Die App liest die Werte beim Anmelden (LoginPage) und speichert jede Änderung sofort (DarstellungSpeicher).
-- NULL = für dieses Konto wurde noch nichts gespeichert; dann gilt die Auswahl auf der Anmeldeseite.
BEGIN;

ALTER TABLE public.benutzer
    ADD COLUMN IF NOT EXISTS darstellung_hell boolean,
    ADD COLUMN IF NOT EXISTS darstellung_akzent text;

ALTER TABLE public.benutzer
    DROP CONSTRAINT IF EXISTS benutzer_darstellung_akzent_check;
ALTER TABLE public.benutzer
    ADD CONSTRAINT benutzer_darstellung_akzent_check
    CHECK (darstellung_akzent IS NULL OR darstellung_akzent ~ '^#[0-9A-F]{6}$');

COMMENT ON COLUMN public.benutzer.darstellung_hell IS
    'Zuletzt gewählte Darstellung: true = hell, false = dunkel. NULL = noch nie gespeichert.';
COMMENT ON COLUMN public.benutzer.darstellung_akzent IS
    'Zuletzt gewählte Akzentfarbe als #RRGGBB (Großbuchstaben). NULL = noch nie gespeichert.';

COMMIT;
