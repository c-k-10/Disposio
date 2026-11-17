-- Entfernt die Demo-Daten aus 016_demo_viele_sendungen.sql wieder (nur die mit diesen Demo-Kennungen):
-- Sendungen A-95…, Touren T-95…, Kunden K-95…, Fahrer …@demo-viele.disposio.test, Fahrzeuge N-DV 2….
-- Auch Touren, die ihr in D02 per Drag & Drop aus diesen Sendungen gebaut habt, werden mit entfernt,
-- sofern sie danach keine anderen Sendungen mehr haben. Abteilungen bleiben stehen.
-- Bitte die gleiche Kennung wie beim Anlegen eintragen.

DO $$
DECLARE
    v_kennung text := 'nordlog';   -- ← HIER die Kennung eurer Testfirma eintragen
    v_firma   bigint;
    v_touren  bigint[];
BEGIN
    SELECT unternehmen_id INTO v_firma FROM public.unternehmen WHERE lower(kennung) = lower(v_kennung);
    IF v_firma IS NULL THEN
        RAISE EXCEPTION 'Keine Firma mit der Kennung % gefunden.', v_kennung;
    END IF;

    -- Touren, die nur aus Demo-Sendungen bestehen (T-95… und selbst gebaute), plus alle T-95….
    SELECT array_agg(DISTINCT t.tour_id) INTO v_touren
    FROM public.tour t
    WHERE t.unternehmen_id = v_firma
      AND (t.tournummer LIKE 'T-95__'
           OR (EXISTS (SELECT 1 FROM public.auftrag a WHERE a.tour_id = t.tour_id AND a.auftragsnummer LIKE 'A-95__')
               AND NOT EXISTS (SELECT 1 FROM public.auftrag a WHERE a.tour_id = t.tour_id AND a.auftragsnummer NOT LIKE 'A-95__')));

    -- Reihenfolge wegen der Fremdschlüssel: Verknüpfungen, Stopps, Sendungen, Touren, Kunden …
    DELETE FROM public.stopp_auftrag sa USING public.tourstopp s
    WHERE sa.stopp_id = s.stopp_id AND s.tour_id = ANY(v_touren);
    DELETE FROM public.stopp_auftrag sa USING public.auftrag a
    WHERE sa.auftrag_id = a.auftrag_id AND a.unternehmen_id = v_firma AND a.auftragsnummer LIKE 'A-95__';
    DELETE FROM public.tourstopp WHERE tour_id = ANY(v_touren);
    UPDATE public.auftrag SET tour_id = NULL WHERE tour_id = ANY(v_touren);
    DELETE FROM public.auftrag WHERE unternehmen_id = v_firma AND auftragsnummer LIKE 'A-95__';
    DELETE FROM public.tour WHERE tour_id = ANY(v_touren);
    DELETE FROM public.kunde WHERE unternehmen_id = v_firma AND kundennummer LIKE 'K-95__';

    -- Demo-Fahrer: Touren mit echten Daten verlieren nur den Fahrer.
    UPDATE public.tour SET fahrer_id = NULL WHERE fahrer_id IN (
        SELECT fa.fahrer_id FROM public.fahrer fa JOIN public.benutzer b ON b.benutzer_id = fa.benutzer_id
        WHERE b.unternehmen_id = v_firma AND b.email LIKE '%@demo-viele.disposio.test');
    DELETE FROM public.fahrer fa USING public.benutzer b
    WHERE fa.benutzer_id = b.benutzer_id AND b.unternehmen_id = v_firma AND b.email LIKE '%@demo-viele.disposio.test';
    DELETE FROM public.benutzer_rolle br USING public.benutzer b
    WHERE br.benutzer_id = b.benutzer_id AND b.unternehmen_id = v_firma AND b.email LIKE '%@demo-viele.disposio.test';
    DELETE FROM public.benutzer WHERE unternehmen_id = v_firma AND email LIKE '%@demo-viele.disposio.test';

    -- Demo-Fahrzeuge: Verweise aus echten Touren lösen, dann löschen.
    UPDATE public.tour SET fahrzeug_id = NULL WHERE fahrzeug_id IN (
        SELECT fahrzeug_id FROM public.fahrzeug WHERE unternehmen_id = v_firma AND kennzeichen LIKE 'N-DV 2__');
    DELETE FROM public.fahrzeug WHERE unternehmen_id = v_firma AND kennzeichen LIKE 'N-DV 2__';

    RAISE NOTICE 'Demo-Daten (viele Sendungen) für Firma % entfernt.', v_kennung;
END $$;
