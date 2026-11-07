-- Einmalige additive Migration für D11 „Frachtdokumente & Abliefernachweise“: Bemerkung zu einem Dokument.
-- Das Programm führt diese Schemaänderung NICHT beim Start aus. Bitte einmal im Neon SQL Editor ausführen.
-- Die Fahrer-App (Disposio.Api) ist davon nicht betroffen: Die Spalte ist optional.
BEGIN;

ALTER TABLE public.dokument
    ADD COLUMN IF NOT EXISTS bemerkung text;

COMMENT ON COLUMN public.dokument.bemerkung IS
    'Optionale Bemerkung beim Zuordnen in der Desktop-App (D11), höchstens 500 Zeichen.';

COMMIT;
