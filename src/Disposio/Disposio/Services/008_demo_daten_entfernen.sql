-- Entfernt die Demo-Daten aus 007_demo_daten.sql wieder (nur die mit den Demo-Kennungen).
-- Abteilungen bleiben stehen. Bitte die gleiche Kennung wie beim Anlegen eintragen.

DO $$
DECLARE
    v_kennung text := 'nordlog';   -- ← HIER die Kennung eurer Testfirma eintragen
    v_firma   bigint;
BEGIN
    SELECT unternehmen_id INTO v_firma FROM public.unternehmen WHERE lower(kennung) = lower(v_kennung);
    IF v_firma IS NULL THEN
        RAISE EXCEPTION 'Keine Firma mit der Kennung % gefunden.', v_kennung;
    END IF;

    -- Reihenfolge wegen der Fremdschlüssel: erst Verknüpfungen, dann Stopps, Sendungen, Touren …
    DELETE FROM public.stopp_auftrag sa
    USING public.tourstopp s, public.tour t
    WHERE sa.stopp_id = s.stopp_id AND s.tour_id = t.tour_id AND t.unternehmen_id = v_firma AND t.tournummer LIKE 'T-90__';
    DELETE FROM public.stopp_auftrag sa
    USING public.auftrag a
    WHERE sa.auftrag_id = a.auftrag_id AND a.unternehmen_id = v_firma AND a.auftragsnummer LIKE 'A-90__';
    DELETE FROM public.tourstopp s USING public.tour t
    WHERE s.tour_id = t.tour_id AND t.unternehmen_id = v_firma AND t.tournummer LIKE 'T-90__';
    DELETE FROM public.auftrag WHERE unternehmen_id = v_firma AND auftragsnummer LIKE 'A-90__';
    -- Echte Sendungen, die in eine Demo-Tour verplant wurden, werden wieder frei.
    UPDATE public.auftrag a SET tour_id = NULL FROM public.tour t
    WHERE a.tour_id = t.tour_id AND t.unternehmen_id = v_firma AND t.tournummer LIKE 'T-90__';
    DELETE FROM public.tour WHERE unternehmen_id = v_firma AND tournummer LIKE 'T-90__';
    DELETE FROM public.kunde WHERE unternehmen_id = v_firma AND kundennummer LIKE 'K-90__';

    -- Demo-Fahrer: Touren, die echte Daten auf sie verweisen, verlieren nur den Fahrer.
    UPDATE public.tour SET fahrer_id = NULL WHERE fahrer_id IN (
        SELECT fa.fahrer_id FROM public.fahrer fa JOIN public.benutzer b ON b.benutzer_id = fa.benutzer_id
        WHERE b.unternehmen_id = v_firma AND b.email LIKE '%@demo.disposio.test');
    DELETE FROM public.fahrer fa USING public.benutzer b
    WHERE fa.benutzer_id = b.benutzer_id AND b.unternehmen_id = v_firma AND b.email LIKE '%@demo.disposio.test';
    DELETE FROM public.benutzer_rolle br USING public.benutzer b
    WHERE br.benutzer_id = b.benutzer_id AND b.unternehmen_id = v_firma AND b.email LIKE '%@demo.disposio.test';
    DELETE FROM public.benutzer WHERE unternehmen_id = v_firma AND email LIKE '%@demo.disposio.test';

    -- Demo-Fahrzeuge: Verweise aus echten Touren lösen, dann löschen.
    UPDATE public.tour SET fahrzeug_id = NULL WHERE fahrzeug_id IN (
        SELECT fahrzeug_id FROM public.fahrzeug WHERE unternehmen_id = v_firma AND kennzeichen LIKE 'N-DS 1__');
    DELETE FROM public.fahrzeug WHERE unternehmen_id = v_firma AND kennzeichen LIKE 'N-DS 1__';

    RAISE NOTICE 'Demo-Daten für Firma % entfernt.', v_kennung;
END $$;
