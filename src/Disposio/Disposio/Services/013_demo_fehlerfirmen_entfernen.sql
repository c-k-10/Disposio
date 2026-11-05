-- Entfernt die drei Demo-Mandanten aus 012_demo_fehlerfirmen.sql vollständig (Kennungen demo-fehler-…).
-- Andere Firmen und Daten bleiben unberührt. Im Neon SQL Editor ausführen.

DO $$
DECLARE
    v_firmen bigint[];
BEGIN
    SELECT array_agg(unternehmen_id) INTO v_firmen FROM public.unternehmen
    WHERE kennung IN ('demo-fehler-uebersetzung', 'demo-fehler-ohne-admin', 'demo-fehler-webspam');
    IF v_firmen IS NULL THEN
        RAISE NOTICE 'Keine Demo-Fehlerfirmen vorhanden.';
        RETURN;
    END IF;

    -- Reihenfolge wegen der Fremdschlüssel: erst Nachrichten und Chats, dann Touren, Fahrer, Konten, Firma.
    DELETE FROM public.nachricht WHERE chat_id IN (
        SELECT c.chat_id FROM public.chat c JOIN public.tour t ON t.tour_id = c.tour_id WHERE t.unternehmen_id = ANY (v_firmen));
    DELETE FROM public.chat WHERE tour_id IN (SELECT tour_id FROM public.tour WHERE unternehmen_id = ANY (v_firmen));
    DELETE FROM public.aktionsprotokoll WHERE unternehmen_id = ANY (v_firmen);
    DELETE FROM public.tour WHERE unternehmen_id = ANY (v_firmen);
    DELETE FROM public.fahrer WHERE benutzer_id IN (SELECT benutzer_id FROM public.benutzer WHERE unternehmen_id = ANY (v_firmen));
    DELETE FROM public.benutzer_rolle WHERE benutzer_id IN (SELECT benutzer_id FROM public.benutzer WHERE unternehmen_id = ANY (v_firmen));
    DELETE FROM public.benutzer WHERE unternehmen_id = ANY (v_firmen);
    DELETE FROM public.abteilung WHERE unternehmen_id = ANY (v_firmen);
    DELETE FROM public.unternehmen WHERE unternehmen_id = ANY (v_firmen);

    RAISE NOTICE 'Entfernt: % Demo-Fehlerfirmen', array_length(v_firmen, 1);
END $$;
