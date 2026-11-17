-- Einmalige additive Migration: ersetzte Dokumente im Verlauf behalten.
-- Das Programm führt diese Schemaänderung NICHT beim Start aus. Bitte einmal im Neon SQL Editor ausführen.
--
-- Wird ein Dokument ersetzt (Knopf „Ersetzen“ in D03 / D16), bleibt die alte Zeile stehen und zeigt auf die neue:
--   ersetzt_durch_id        → die neue Version
--   ersetzt_am              → wann ersetzt wurde
--   ersetzt_von_benutzer_id → wer ersetzt hat
-- Aktuell ist ein Dokument, wenn ersetzt_durch_id leer ist. Ersetzte Versionen können nicht gelöscht werden.
-- Die Fahrer-App (Disposio.Api) ist davon nicht betroffen: Die Spalten sind optional.
BEGIN;

ALTER TABLE public.dokument
    ADD COLUMN IF NOT EXISTS ersetzt_durch_id bigint REFERENCES public.dokument (dokument_id),
    ADD COLUMN IF NOT EXISTS ersetzt_am timestamp with time zone,
    ADD COLUMN IF NOT EXISTS ersetzt_von_benutzer_id bigint REFERENCES public.benutzer (benutzer_id);

-- Eine Version kann nur einmal ersetzt werden, und nur durch ein anderes Dokument.
CREATE UNIQUE INDEX IF NOT EXISTS dokument_ersetzt_durch_eindeutig
    ON public.dokument (ersetzt_durch_id) WHERE ersetzt_durch_id IS NOT NULL;

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'dokument_ersetzt_nicht_selbst') THEN
        ALTER TABLE public.dokument
            ADD CONSTRAINT dokument_ersetzt_nicht_selbst CHECK (ersetzt_durch_id IS NULL OR ersetzt_durch_id <> dokument_id);
    END IF;
END $$;

COMMENT ON COLUMN public.dokument.ersetzt_durch_id IS
    'Neuere Version dieses Dokuments (Verlauf). Leer = aktuelle Version.';
COMMENT ON COLUMN public.dokument.ersetzt_am IS 'Zeitpunkt, zu dem das Dokument ersetzt wurde.';
COMMENT ON COLUMN public.dokument.ersetzt_von_benutzer_id IS 'Konto, das das Dokument ersetzt hat.';

COMMIT;
