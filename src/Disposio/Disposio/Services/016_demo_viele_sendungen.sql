-- Zusätzliche Demo-Daten, um D02 „Sendungen“ mit VIELEN Sendungen anzusehen. NUR für die Test-Datenbank.
--
-- So geht's:
--   1. Unten bei v_kennung die Unternehmenskennung eurer Testfirma eintragen (die vom Login, z. B. 'nordlog').
--   2. Das ganze Skript im Neon SQL Editor ausführen.
-- Legt an (Tage hängen vom Ausführungstag ab: heute, morgen, übermorgen):
--   - 8 Kunden (K-95…), 4 Fahrer (…@demo-viele.disposio.test), 4 Fahrzeuge (N-DV 2…)
--   - 36 Sendungen (A-9501 … A-9536), je 12 pro Tag
--   - 4 Touren (T-9501 … T-9504) mit je 1–3 dieser Sendungen:
--       T-9501 heute, zugewiesen · T-9502 heute, geplant ohne Fahrer/Fahrzeug
--       T-9503 morgen, zugewiesen · T-9504 morgen, geplant nur mit Fahrer
--     Die übrigen gut 25 Sendungen sind offen („Noch zu verplanen“).
-- Unabhängig von 007 (andere Nummern). Entfernen mit 017_demo_viele_sendungen_entfernen.sql.
-- Die Demo-Fahrer haben ein unbekanntes Zufallspasswort und können sich nicht anmelden.
-- Voraussetzung: 006_abteilung_aktiv.sql wurde ausgeführt.

DO $$
DECLARE
    v_kennung   text := 'nordlog';   -- ← HIER die Kennung eurer Testfirma eintragen

    v_firma     bigint;
    v_heute     date := (now() AT TIME ZONE 'Europe/Berlin')::date;
    v_frische   bigint;
    v_trocken   bigint;
    v_rolle     bigint;
    v_hash      text := '$2a$11$M1fUFSAUMkF5deLMOrpUTuBQKeBUmb4SsnhCp/.9gI1qwXN9M0GQm'; -- Zufallspasswort, niemand kennt es
    v_depot     text := 'Hafenstraße 25, 90451 Nürnberg';
    v_b         bigint;
    v_fahrer    bigint[] := '{}';
    v_fahrzeug  bigint[] := '{}';
    v_kunden    bigint[] := '{}';
    v_tour      bigint;
    v_s         bigint;
    v_i         integer;
    v_k         integer;
    v_tag       date;
    v_frisch    boolean;
    v_anzahl    integer;
    v_verpackung text;
    v_laenge    numeric;
    v_breite    numeric;
    v_ab_von    time;
    v_li_von    time;
    v_nr        integer;

    -- Kunden: Name, Ansprechpartner, Straße, PLZ, Ort, Referenz-Kürzel
    c_name   text[] := ARRAY['Frischemarkt Süd', 'Hotel Am Main', 'Metzgerei Bauer', 'Brauhaus Lauf',
                             'Feinkost Albrecht', 'Baumarkt Ost', 'Kantine Campus Süd', 'Getränkemarkt Roth'];
    c_person text[] := ARRAY['Frau Keller', 'Herr Brandt', 'Herr Bauer', 'Frau Roth',
                             'Frau Albrecht', 'Herr Weiß', 'Frau Demir', 'Herr Vogel'];
    c_str    text[] := ARRAY['Fürther Straße 210', 'Mainkai 4', 'Marktplatz 7', 'Hersbrucker Straße 22',
                             'Domplatz 3', 'Regensburger Straße 300', 'Am Weichselgarten 12', 'Allersberger Straße 45'];
    c_plz    text[] := ARRAY['90429', '97070', '91522', '91207', '96049', '90480', '91058', '91154'];
    c_ort    text[] := ARRAY['Nürnberg', 'Würzburg', 'Ansbach', 'Lauf an der Pegnitz', 'Bamberg', 'Nürnberg', 'Erlangen', 'Roth'];
    c_kurz   text[] := ARRAY['FS', 'HM', 'MB', 'BL', 'FA', 'BO', 'KC', 'GR'];
    c_frisch boolean[] := ARRAY[true, true, true, false, true, false, true, false];

    p_prio   text[] := ARRAY['Normal', 'Hoch', 'Normal', 'Niedrig', 'Express', 'Normal'];
    p_equip  text[] := ARRAY['Keines', 'Hebebühne', 'Stapler vor Ort', 'Hubwagen'];
BEGIN
    SELECT unternehmen_id INTO v_firma FROM public.unternehmen WHERE lower(kennung) = lower(v_kennung);
    IF v_firma IS NULL THEN
        RAISE EXCEPTION 'Keine Firma mit der Kennung % gefunden. Bitte v_kennung oben anpassen.', v_kennung;
    END IF;
    IF EXISTS (SELECT 1 FROM public.auftrag WHERE unternehmen_id = v_firma AND auftragsnummer = 'A-9501') THEN
        RAISE NOTICE 'Diese Demo-Daten sind für die Firma schon vorhanden. Zum Neu-Anlegen zuerst 017_demo_viele_sendungen_entfernen.sql ausführen.';
        RETURN;
    END IF;

    -- ── Abteilungen (nur anlegen, wenn es sie noch nicht gibt) ──────────────────────────
    INSERT INTO public.abteilung (bezeichnung, unternehmen_id)
    SELECT n, v_firma FROM unnest(ARRAY['Frische', 'Trockengut']) AS n
    WHERE NOT EXISTS (SELECT 1 FROM public.abteilung a WHERE a.unternehmen_id = v_firma AND a.bezeichnung = n);
    SELECT abteilung_id INTO v_frische FROM public.abteilung WHERE unternehmen_id = v_firma AND bezeichnung = 'Frische';
    SELECT abteilung_id INTO v_trocken FROM public.abteilung WHERE unternehmen_id = v_firma AND bezeichnung = 'Trockengut';

    -- ── 4 Fahrer ────────────────────────────────────────────────────────────────────────
    SELECT rollen_id INTO v_rolle FROM public.rolle WHERE bezeichnung = 'Fahrer' AND unternehmen_id IS NULL LIMIT 1;
    FOR v_i IN 1..4 LOOP
        INSERT INTO public.benutzer (vorname, nachname, email, passwort_hash, unternehmen_id, abteilung_id, sprache, aktiv)
        VALUES ((ARRAY['Tom', 'Anna', 'Mehmet', 'Ivana'])[v_i], (ARRAY['Becker', 'Wolf', 'Arslan', 'Horvat'])[v_i],
                (ARRAY['tom.becker', 'anna.wolf', 'mehmet.arslan', 'ivana.horvat'])[v_i] || '@demo-viele.disposio.test',
                v_hash, v_firma, CASE WHEN v_i % 2 = 1 THEN v_frische ELSE v_trocken END,
                (ARRAY['de', 'de', 'tr', 'hr'])[v_i], true)
        RETURNING benutzer_id INTO v_b;
        IF v_rolle IS NOT NULL THEN
            INSERT INTO public.benutzer_rolle (benutzer_id, rollen_id) VALUES (v_b, v_rolle);
        END IF;
        INSERT INTO public.fahrer (fuehrerscheinnummer, fuehrerscheinklasse, eintrittsdatum, verfuegbarkeit, benutzer_id)
        VALUES ('DEMO-V' || (700000 + v_i * 1371)::text, (ARRAY['CE', 'C', 'CE', 'CE'])[v_i], v_heute - 900 * v_i, 'Verfügbar', v_b)
        RETURNING fahrer_id INTO v_s;
        v_fahrer := v_fahrer || v_s;
    END LOOP;

    -- ── 4 Fahrzeuge ─────────────────────────────────────────────────────────────────────
    INSERT INTO public.fahrzeug (kennzeichen, fahrzeugtyp, hersteller, modell, baujahr, kilometerstand, status, tuev_datum,
        aufbau, nutzlast_kg, nutzbare_laenge_m, innenbreite_m, innenhoehe_m, nutzbare_lademeter, ep_stellplaetze,
        solltemperatur_c, unternehmen_id, abteilung_id)
    VALUES
        ('N-DV 201', '40 t', 'Scania', 'R 450', 2022, 121400, 'Verfügbar', v_heute + 300, 'Frische',
         24000, 13.6, 2.46, 2.6, 13.6, 33, 3, v_firma, v_frische),
        ('N-DV 202', '26 t', 'Volvo', 'FM 420', 2019, 288100, 'Verfügbar', v_heute + 150, 'Trockengut',
         14500, 9.6, 2.48, 2.7, 9.6, 23, NULL, v_firma, v_trocken),
        ('N-DV 203', '18 t', 'MAN', 'TGM 18.320', 2021, 97300, 'Verfügbar', v_heute + 220, 'Trockengut',
         9800, 8.2, 2.45, 2.55, 8.2, 20, NULL, v_firma, v_trocken),
        ('N-DV 204', '12 t', 'Mercedes-Benz', 'Atego 1224', 2023, 41200, 'Verfügbar', v_heute + 500, 'Frische',
         5600, 7.2, 2.4, 2.4, 7.2, 17, 4, v_firma, v_frische);
    SELECT array_agg(fahrzeug_id ORDER BY kennzeichen) INTO v_fahrzeug
    FROM public.fahrzeug WHERE unternehmen_id = v_firma AND kennzeichen LIKE 'N-DV 2__';

    -- ── 8 Kunden ────────────────────────────────────────────────────────────────────────
    FOR v_k IN 1..8 LOOP
        INSERT INTO public.kunde (kundennummer, firmenname, ansprechpartner, adresse, plz, ort, telefon, email, unternehmen_id)
        VALUES ('K-95' || lpad(v_k::text, 2, '0'), c_name[v_k], c_person[v_k], c_str[v_k], c_plz[v_k], c_ort[v_k],
                '0911 ' || (500100 + v_k * 37)::text, 'kontakt' || v_k || '@demo-viele.disposio.test', v_firma)
        RETURNING kunde_id INTO v_s;
        v_kunden := v_kunden || v_s;
    END LOOP;

    -- ── 36 Sendungen: je 12 für heute, morgen, übermorgen ───────────────────────────────
    FOR v_i IN 1..36 LOOP
        v_k := ((v_i * 5) % 8) + 1;                      -- Kunden gemischt
        v_tag := v_heute + ((v_i - 1) / 12);
        v_frisch := c_frisch[v_k];
        v_anzahl := 1 + (v_i * 7) % 14;
        v_verpackung := CASE WHEN v_i % 7 = 0 THEN 'Karton' WHEN v_i % 5 = 0 THEN 'Gitterbox' ELSE 'Europalette' END;
        v_laenge := CASE v_verpackung WHEN 'Karton' THEN 0.6 ELSE 1.2 END;
        v_breite := CASE v_verpackung WHEN 'Karton' THEN 0.4 WHEN 'Gitterbox' THEN 1.0 ELSE 0.8 END;
        v_ab_von := TIME '06:00' + make_interval(hours => (v_i % 7));               -- 06:00 … 12:00
        v_li_von := v_ab_von + make_interval(hours => 2 + (v_i % 3));               -- 2–4 h später

        INSERT INTO public.auftrag (auftragsnummer, kunde_id, kundenreferenz, abholadresse, lieferadresse, abholdatum, lieferdatum,
            abholfenster_von, abholfenster_bis, lieferfenster_von, lieferfenster_bis, gewicht_kg, laenge_m, breite_m, hoehe_m,
            verpackungstyp, anzahl, gutart, temperatur_min_c, temperatur_max_c, stapelbar, drehbar, gefahrgut, warenwert, versicherung,
            ansprechpartner, avisierung_erforderlich, ladehilfsmitteltausch, lade_entladeequipment, zollrelevanz, status, prioritaet,
            unternehmen_id, tour_id)
        VALUES ('A-95' || lpad(v_i::text, 2, '0'), v_kunden[v_k], c_kurz[v_k] || '-' || (3100 + v_i * 13)::text,
                CASE WHEN v_i % 4 = 0 THEN 'Gewerbering 4, 90427 Nürnberg' ELSE v_depot END,
                c_str[v_k] || ', ' || c_plz[v_k] || ' ' || c_ort[v_k],
                v_tag, v_tag, v_ab_von, v_ab_von + interval '1 hour', v_li_von, v_li_von + interval '1 hour',
                v_anzahl * (180 + (v_i * 37) % 300), v_laenge, v_breite, 1.0 + (v_i % 6) * 0.15,
                v_verpackung, v_anzahl, CASE WHEN v_frisch THEN 'Frische' ELSE 'Trockengut' END,
                CASE WHEN v_frisch THEN 2 END, CASE WHEN v_frisch THEN 7 END,
                v_i % 3 = 0, v_verpackung = 'Karton', v_i % 11 = 0, v_anzahl * 450, v_i % 4 = 1,
                c_person[v_k], v_i % 3 = 1, v_verpackung = 'Europalette', p_equip[(v_i % 4) + 1], false,
                'Offen', p_prio[(v_i % 6) + 1], v_firma, NULL);
    END LOOP;

    -- ── 4 Touren mit je 1–3 dieser Sendungen ─────────────────────────────────────────────
    -- Je Tour: Abholung im Depot (alle Sendungen), danach eine Zustellung je Sendung.
    FOR v_nr IN 1..4 LOOP
        v_tag := v_heute + CASE WHEN v_nr <= 2 THEN 0 ELSE 1 END;
        INSERT INTO public.tour (tournummer, startzeit, endzeit, startort, zielort, status, unternehmen_id, abteilung_id,
                                 fahrer_id, fahrzeug_id)
        VALUES ('T-950' || v_nr,
                (v_tag + TIME '05:30') AT TIME ZONE 'Europe/Berlin', (v_tag + TIME '19:30') AT TIME ZONE 'Europe/Berlin',
                v_depot, v_depot,
                CASE WHEN v_nr IN (1, 3) THEN 'Zugewiesen' ELSE 'Geplant' END,
                v_firma, CASE WHEN v_nr IN (1, 2) THEN v_frische ELSE v_trocken END,
                CASE v_nr WHEN 1 THEN v_fahrer[1] WHEN 3 THEN v_fahrer[3] WHEN 4 THEN v_fahrer[2] END,
                CASE v_nr WHEN 1 THEN v_fahrzeug[1] WHEN 3 THEN v_fahrzeug[2] END)
        RETURNING tour_id INTO v_tour;

        -- Sendungen der Tour: passende Gutart am Tag der Tour (Tour 1/2: heute Frische, 3/4: morgen Trockengut).
        UPDATE public.auftrag a SET tour_id = v_tour, status = 'Verplant'
        WHERE a.auftrag_id IN (
            SELECT x.auftrag_id FROM public.auftrag x
            WHERE x.unternehmen_id = v_firma AND x.auftragsnummer LIKE 'A-95__' AND x.tour_id IS NULL
              AND x.abholdatum = v_tag
              AND x.gutart = CASE WHEN v_nr IN (1, 2) THEN 'Frische' ELSE 'Trockengut' END
            ORDER BY x.auftragsnummer
            LIMIT CASE WHEN v_nr IN (1, 3) THEN 3 ELSE 2 END);

        INSERT INTO public.tourstopp (tour_id, reihenfolge, art, bezeichnung, adresse, zeitfenster_von, zeitfenster_bis, status)
        VALUES (v_tour, 1, 'Abholung', 'Depot Nürnberg Hafen', v_depot,
                (v_tag + TIME '05:30') AT TIME ZONE 'Europe/Berlin', (v_tag + TIME '07:00') AT TIME ZONE 'Europe/Berlin', 'Offen')
        RETURNING stopp_id INTO v_s;
        INSERT INTO public.stopp_auftrag (stopp_id, auftrag_id, vorgang_art)
        SELECT v_s, auftrag_id, 'Abholung' FROM public.auftrag WHERE tour_id = v_tour;

        v_i := 1;
        FOR v_s IN SELECT auftrag_id FROM public.auftrag WHERE tour_id = v_tour ORDER BY lieferfenster_von, auftragsnummer LOOP
            v_i := v_i + 1;
            WITH st AS (
                INSERT INTO public.tourstopp (tour_id, reihenfolge, art, bezeichnung, adresse, zeitfenster_von, zeitfenster_bis, status)
                SELECT v_tour, v_i, 'Zustellung', k.firmenname, a.lieferadresse,
                       (a.lieferdatum + a.lieferfenster_von) AT TIME ZONE 'Europe/Berlin',
                       (a.lieferdatum + a.lieferfenster_bis) AT TIME ZONE 'Europe/Berlin', 'Offen'
                FROM public.auftrag a JOIN public.kunde k ON k.kunde_id = a.kunde_id
                WHERE a.auftrag_id = v_s
                RETURNING stopp_id
            )
            INSERT INTO public.stopp_auftrag (stopp_id, auftrag_id, vorgang_art)
            SELECT st.stopp_id, v_s, 'Zustellung' FROM st;
        END LOOP;
    END LOOP;

    RAISE NOTICE 'Demo-Daten für Firma % angelegt: 8 Kunden, 4 Fahrer, 4 Fahrzeuge, 36 Sendungen, 4 Touren.', v_kennung;
END $$;
