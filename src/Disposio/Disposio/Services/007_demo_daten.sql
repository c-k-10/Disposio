-- Demo-Daten zum Ausprobieren: Kunden, Fahrer, Fahrzeuge, Sendungen und Touren für EINE Firma.
-- NUR für die Entwicklungs-/Testdatenbank gedacht, nicht für echte Kundendaten.
--
-- So geht's:
--   1. Unten bei v_kennung die Unternehmenskennung eurer Testfirma eintragen (die vom Login, z. B. 'nordlog').
--   2. Das ganze Skript im Neon SQL Editor ausführen.
-- Alle Zeiten hängen vom Tag ab, an dem das Skript läuft: Die Touren sind „heute“ und „morgen“.
-- Erkennbar an: Kennzeichen N-DS …, Kundennummern K-9…, Auftragsnummern A-9…, Tournummern T-9…,
-- E-Mails …@demo.disposio.test. Entfernen mit 008_demo_daten_entfernen.sql.
-- Die Demo-Fahrer haben ein unbekanntes Zufallspasswort und können sich nicht anmelden.
-- Voraussetzung: 006_abteilung_aktiv.sql wurde ausgeführt.

DO $$
DECLARE
    v_kennung   text := 'nordlog';   -- ← HIER die Kennung eurer Testfirma eintragen

    v_firma     bigint;
    v_heute     date := (now() AT TIME ZONE 'Europe/Berlin')::date;
    v_morgen    date := (now() AT TIME ZONE 'Europe/Berlin')::date + 1;
    v_jetzt     timestamptz := now();
    v_frische   bigint;
    v_trocken   bigint;
    v_rolle     bigint;
    v_hash      text := '$2a$11$M1fUFSAUMkF5deLMOrpUTuBQKeBUmb4SsnhCp/.9gI1qwXN9M0GQm'; -- Zufallspasswort, niemand kennt es
    v_b         bigint;
    v_jan       bigint; v_lea bigint; v_piotr bigint; v_sarah bigint;          -- fahrer_id
    v_fz1       bigint; v_fz2 bigint; v_fz3 bigint; v_fz4 bigint;              -- fahrzeug_id
    v_k1        bigint; v_k2 bigint; v_k3 bigint; v_k4 bigint;                 -- kunde_id
    v_t1        bigint; v_t2 bigint; v_t3 bigint; v_t4 bigint;                 -- tour_id
    v_s         bigint;
    v_depot     text := 'Hafenstraße 25, 90451 Nürnberg';
BEGIN
    SELECT unternehmen_id INTO v_firma FROM public.unternehmen WHERE lower(kennung) = lower(v_kennung);
    IF v_firma IS NULL THEN
        RAISE EXCEPTION 'Keine Firma mit der Kennung % gefunden. Bitte v_kennung oben anpassen.', v_kennung;
    END IF;
    IF EXISTS (SELECT 1 FROM public.tour WHERE unternehmen_id = v_firma AND tournummer = 'T-9001') THEN
        RAISE NOTICE 'Die Demo-Daten sind für diese Firma schon vorhanden. Zum Neu-Anlegen zuerst 008_demo_daten_entfernen.sql ausführen.';
        RETURN;
    END IF;

    -- ── Abteilungen (nur anlegen, wenn es sie noch nicht gibt) ──────────────────────────
    INSERT INTO public.abteilung (bezeichnung, unternehmen_id)
    SELECT n, v_firma FROM unnest(ARRAY['Frische', 'Trockengut']) AS n
    WHERE NOT EXISTS (SELECT 1 FROM public.abteilung a WHERE a.unternehmen_id = v_firma AND a.bezeichnung = n);
    SELECT abteilung_id INTO v_frische FROM public.abteilung WHERE unternehmen_id = v_firma AND bezeichnung = 'Frische';
    SELECT abteilung_id INTO v_trocken FROM public.abteilung WHERE unternehmen_id = v_firma AND bezeichnung = 'Trockengut';

    -- ── Fahrer: Konto in benutzer + Rolle „Fahrer“ + Eintrag in fahrer ──────────────────
    SELECT rollen_id INTO v_rolle FROM public.rolle WHERE bezeichnung = 'Fahrer' AND unternehmen_id IS NULL LIMIT 1;

    INSERT INTO public.benutzer (vorname, nachname, email, passwort_hash, unternehmen_id, abteilung_id, sprache, aktiv)
    VALUES ('Jan', 'Kowalski', 'jan.kowalski@demo.disposio.test', v_hash, v_firma, v_frische, 'pl', true) RETURNING benutzer_id INTO v_b;
    IF v_rolle IS NOT NULL THEN INSERT INTO public.benutzer_rolle (benutzer_id, rollen_id) VALUES (v_b, v_rolle); END IF;
    INSERT INTO public.fahrer (fuehrerscheinnummer, fuehrerscheinklasse, eintrittsdatum, verfuegbarkeit, benutzer_id)
    VALUES ('DEMO-B072041', 'CE', DATE '2019-03-01', 'Verfügbar', v_b) RETURNING fahrer_id INTO v_jan;

    INSERT INTO public.benutzer (vorname, nachname, email, passwort_hash, unternehmen_id, abteilung_id, sprache, aktiv)
    VALUES ('Lea', 'Berger', 'lea.berger@demo.disposio.test', v_hash, v_firma, v_trocken, 'de', true) RETURNING benutzer_id INTO v_b;
    IF v_rolle IS NOT NULL THEN INSERT INTO public.benutzer_rolle (benutzer_id, rollen_id) VALUES (v_b, v_rolle); END IF;
    INSERT INTO public.fahrer (fuehrerscheinnummer, fuehrerscheinklasse, eintrittsdatum, verfuegbarkeit, benutzer_id)
    VALUES ('DEMO-N118734', 'CE', DATE '2021-07-15', 'Verfügbar', v_b) RETURNING fahrer_id INTO v_lea;

    INSERT INTO public.benutzer (vorname, nachname, email, passwort_hash, unternehmen_id, abteilung_id, sprache, aktiv)
    VALUES ('Piotr', 'Nowak', 'piotr.nowak@demo.disposio.test', v_hash, v_firma, v_frische, 'pl', true) RETURNING benutzer_id INTO v_b;
    IF v_rolle IS NOT NULL THEN INSERT INTO public.benutzer_rolle (benutzer_id, rollen_id) VALUES (v_b, v_rolle); END IF;
    INSERT INTO public.fahrer (fuehrerscheinnummer, fuehrerscheinklasse, eintrittsdatum, verfuegbarkeit, benutzer_id)
    VALUES ('DEMO-K553190', 'C', DATE '2022-01-10', 'Verfügbar', v_b) RETURNING fahrer_id INTO v_piotr;

    INSERT INTO public.benutzer (vorname, nachname, email, passwort_hash, unternehmen_id, abteilung_id, sprache, aktiv)
    VALUES ('Sarah', 'Klein', 'sarah.klein@demo.disposio.test', v_hash, v_firma, v_trocken, 'en', true) RETURNING benutzer_id INTO v_b;
    IF v_rolle IS NOT NULL THEN INSERT INTO public.benutzer_rolle (benutzer_id, rollen_id) VALUES (v_b, v_rolle); END IF;
    INSERT INTO public.fahrer (fuehrerscheinnummer, fuehrerscheinklasse, eintrittsdatum, verfuegbarkeit, benutzer_id)
    VALUES ('DEMO-E900215', 'CE', DATE '2018-09-01', 'Urlaub', v_b) RETURNING fahrer_id INTO v_sarah;

    -- ── Fahrzeuge ───────────────────────────────────────────────────────────────────────
    INSERT INTO public.fahrzeug (kennzeichen, fahrzeugtyp, hersteller, modell, baujahr, kilometerstand, status, tuev_datum,
        aufbau, nutzlast_kg, nutzbare_laenge_m, innenbreite_m, innenhoehe_m, nutzbare_lademeter, ep_stellplaetze,
        solltemperatur_c, unternehmen_id, abteilung_id)
    VALUES ('N-DS 101', '40 t', 'MAN', 'TGX 18.470', 2021, 184250, 'Im Einsatz', v_heute + 210, 'Frische',
            24000, 13.6, 2.46, 2.6, 13.6, 33, 4, v_firma, v_frische) RETURNING fahrzeug_id INTO v_fz1;
    INSERT INTO public.fahrzeug (kennzeichen, fahrzeugtyp, hersteller, modell, baujahr, kilometerstand, status, tuev_datum,
        aufbau, nutzlast_kg, nutzbare_laenge_m, innenbreite_m, innenhoehe_m, nutzbare_lademeter, ep_stellplaetze,
        solltemperatur_c, unternehmen_id, abteilung_id)
    VALUES ('N-DS 102', '26 t', 'Mercedes-Benz', 'Actros 2545', 2020, 231900, 'Im Einsatz', v_heute + 95, 'Trockengut',
            14500, 9.6, 2.48, 2.7, 9.6, 23, NULL, v_firma, v_trocken) RETURNING fahrzeug_id INTO v_fz2;
    INSERT INTO public.fahrzeug (kennzeichen, fahrzeugtyp, hersteller, modell, baujahr, kilometerstand, status, tuev_datum,
        aufbau, nutzlast_kg, nutzbare_laenge_m, innenbreite_m, innenhoehe_m, nutzbare_lademeter, ep_stellplaetze,
        solltemperatur_c, unternehmen_id, abteilung_id)
    VALUES ('N-DS 103', '18 t', 'DAF', 'XD 450', 2023, 68400, 'Verfügbar', v_heute + 400, 'Frische',
            9800, 8.2, 2.45, 2.55, 8.2, 20, 2, v_firma, v_frische) RETURNING fahrzeug_id INTO v_fz3;
    INSERT INTO public.fahrzeug (kennzeichen, fahrzeugtyp, hersteller, modell, baujahr, kilometerstand, status, tuev_datum,
        aufbau, nutzlast_kg, nutzbare_laenge_m, innenbreite_m, innenhoehe_m, nutzbare_lademeter, ep_stellplaetze,
        solltemperatur_c, unternehmen_id, abteilung_id)
    VALUES ('N-DS 104', '7,5 t', 'Iveco', 'Eurocargo 75E', 2017, 312700, 'Wartung', v_heute + 20, 'Trockengut',
            2900, 6.1, 2.4, 2.3, 6.1, 15, NULL, v_firma, v_trocken) RETURNING fahrzeug_id INTO v_fz4;

    -- ── Kunden ──────────────────────────────────────────────────────────────────────────
    INSERT INTO public.kunde (kundennummer, firmenname, ansprechpartner, adresse, plz, ort, telefon, email, unternehmen_id)
    VALUES ('K-9001', 'Markt Nord', 'Frau Meier', 'Schwabacher Straße 120', '90763', 'Fürth', '0911 700100', 'einkauf@markt-nord.demo.disposio.test', v_firma)
    RETURNING kunde_id INTO v_k1;
    INSERT INTO public.kunde (kundennummer, firmenname, ansprechpartner, adresse, plz, ort, telefon, email, unternehmen_id)
    VALUES ('K-9002', 'Bistro Mitte', 'Herr Yilmaz', 'Königstraße 15', '90402', 'Nürnberg', '0911 222333', 'kontakt@bistro-mitte.demo.disposio.test', v_firma)
    RETURNING kunde_id INTO v_k2;
    INSERT INTO public.kunde (kundennummer, firmenname, ansprechpartner, adresse, plz, ort, telefon, email, unternehmen_id)
    VALUES ('K-9003', 'Bäckerei Sonne', 'Frau Schmidt', 'Hauptstraße 40', '91054', 'Erlangen', '09131 55440', 'bestellung@baeckerei-sonne.demo.disposio.test', v_firma)
    RETURNING kunde_id INTO v_k3;
    INSERT INTO public.kunde (kundennummer, firmenname, ansprechpartner, adresse, plz, ort, telefon, email, unternehmen_id)
    VALUES ('K-9004', 'Getränke Huber', 'Herr Huber', 'Nördlinger Straße 8', '91126', 'Schwabach', '09122 8890', 'lager@getraenke-huber.demo.disposio.test', v_firma)
    RETURNING kunde_id INTO v_k4;

    -- ── Touren ──────────────────────────────────────────────────────────────────────────
    -- T-9001: heute, Frische, unterwegs, nächster Stopp VERSPÄTET (ETA nach Zeitfensterende).
    INSERT INTO public.tour (tournummer, startzeit, endzeit, startort, zielort, status, unternehmen_id, abteilung_id, fahrer_id, fahrzeug_id)
    VALUES ('T-9001', LEAST((v_heute + TIME '06:00') AT TIME ZONE 'Europe/Berlin', v_jetzt - interval '3 hours'),
            GREATEST((v_heute + TIME '18:00') AT TIME ZONE 'Europe/Berlin', v_jetzt + interval '4 hours'),
            'Depot Nürnberg Hafen', 'Depot Nürnberg Hafen', 'Unterwegs', v_firma, v_frische, v_jan, v_fz1)
    RETURNING tour_id INTO v_t1;
    -- T-9002: heute, Trockengut, unterwegs, pünktlich.
    INSERT INTO public.tour (tournummer, startzeit, endzeit, startort, zielort, status, unternehmen_id, abteilung_id, fahrer_id, fahrzeug_id)
    VALUES ('T-9002', LEAST((v_heute + TIME '07:00') AT TIME ZONE 'Europe/Berlin', v_jetzt - interval '2 hours'),
            GREATEST((v_heute + TIME '17:00') AT TIME ZONE 'Europe/Berlin', v_jetzt + interval '5 hours'),
            'Depot Nürnberg Hafen', 'Schwabach', 'Unterwegs', v_firma, v_trocken, v_lea, v_fz2)
    RETURNING tour_id INTO v_t2;
    -- T-9003: heute, Frische, geplant, noch ohne Fahrer und Fahrzeug (→ „Zuweisen“).
    INSERT INTO public.tour (tournummer, startzeit, endzeit, startort, zielort, status, unternehmen_id, abteilung_id)
    VALUES ('T-9003', (v_heute + TIME '13:00') AT TIME ZONE 'Europe/Berlin', (v_heute + TIME '19:00') AT TIME ZONE 'Europe/Berlin',
            'Depot Nürnberg Hafen', 'Depot Nürnberg Hafen', 'Geplant', v_firma, v_frische)
    RETURNING tour_id INTO v_t3;
    -- T-9004: morgen, Trockengut, zugewiesen.
    INSERT INTO public.tour (tournummer, startzeit, endzeit, startort, zielort, status, unternehmen_id, abteilung_id, fahrer_id, fahrzeug_id)
    VALUES ('T-9004', (v_morgen + TIME '06:30') AT TIME ZONE 'Europe/Berlin', (v_morgen + TIME '15:00') AT TIME ZONE 'Europe/Berlin',
            'Depot Nürnberg Hafen', 'Depot Nürnberg Hafen', 'Zugewiesen', v_firma, v_trocken, v_piotr, v_fz3)
    RETURNING tour_id INTO v_t4;

    -- ── Sendungen (Aufträge) ────────────────────────────────────────────────────────────
    -- Spalten: Nummer, Kunde, Referenz, Abholung, Zustellung, Datum, Fenster, Ladegut, Status, Priorität, Tour.
    INSERT INTO public.auftrag (auftragsnummer, kunde_id, kundenreferenz, abholadresse, lieferadresse, abholdatum, lieferdatum,
        abholfenster_von, abholfenster_bis, lieferfenster_von, lieferfenster_bis, gewicht_kg, laenge_m, breite_m, hoehe_m,
        verpackungstyp, anzahl, gutart, temperatur_min_c, temperatur_max_c, stapelbar, drehbar, gefahrgut, warenwert, versicherung,
        ansprechpartner, avisierung_erforderlich, ladehilfsmitteltausch, lade_entladeequipment, zollrelevanz, status, prioritaet,
        unternehmen_id, tour_id)
    VALUES
    -- verplant in T-9001 (Frische)
    ('A-9001', v_k1, 'KD-4821', v_depot, 'Schwabacher Straße 120, 90763 Fürth', v_heute, v_heute, '06:00', '07:00', '10:00', '10:30',
     1600, 1.2, 0.8, 1.6, 'Europalette', 4, 'Frische', 2, 7, false, false, false, 8400, true, 'Frau Meier, 0911 700100', true, true, 'Hebebühne', false, 'Verplant', 'Hoch', v_firma, v_t1),
    ('A-9002', v_k3, 'BS-2207', v_depot, 'Hauptstraße 40, 91054 Erlangen', v_heute, v_heute, '06:00', '07:00', '12:00', '13:00',
     480, 1.2, 0.8, 1.2, 'Europalette', 2, 'Frische', 2, 7, false, false, false, 2100, false, 'Frau Schmidt', false, true, 'Hubwagen', false, 'Verplant', 'Normal', v_firma, v_t1),
    -- verplant in T-9002 (Trockengut)
    ('A-9003', v_k2, 'BM-118', v_depot, 'Königstraße 15, 90402 Nürnberg', v_heute, v_heute, '07:00', '08:00', '09:30', '10:30',
     320, 1.2, 0.8, 1.0, 'Europalette', 1, 'Trockengut', NULL, NULL, true, false, false, 950, false, 'Herr Yilmaz', false, false, 'Keines', false, 'Verplant', 'Normal', v_firma, v_t2),
    ('A-9004', v_k4, 'GH-5560', v_depot, 'Nördlinger Straße 8, 91126 Schwabach', v_heute, v_heute, '07:00', '08:00', '13:00', '15:00',
     5200, 1.2, 0.8, 1.4, 'Europalette', 8, 'Trockengut', NULL, NULL, false, false, false, 6400, true, 'Herr Huber, 09122 8890', true, true, 'Stapler vor Ort', false, 'Verplant', 'Hoch', v_firma, v_t2),
    -- unverplant, heute (für D02 „Manuell verplanen“ / D05)
    ('A-9005', v_k1, 'KD-4822', v_depot, 'Schwabacher Straße 120, 90763 Fürth', v_heute, v_heute, '13:00', '14:00', '15:00', '16:00',
     1200, 1.2, 0.8, 1.5, 'Europalette', 3, 'Frische', 2, 7, false, false, false, 5100, true, 'Frau Meier', true, true, 'Hebebühne', false, 'Offen', 'Normal', v_firma, NULL),
    ('A-9006', v_k2, 'BM-119', v_depot, 'Königstraße 15, 90402 Nürnberg', v_heute, v_heute, '13:00', '14:00', '16:00', '17:00',
     150, 1.2, 0.8, 0.9, 'Europalette', 1, 'Frische', 0, 4, false, false, false, 780, false, 'Herr Yilmaz', false, false, 'Keines', false, 'Offen', 'Express', v_firma, NULL),
    ('A-9007', v_k3, 'BS-2208', 'Gewerbering 4, 90427 Nürnberg', 'Hauptstraße 40, 91054 Erlangen', v_heute, v_heute, '14:00', '15:00', '17:00', '18:00',
     260, 0.6, 0.4, 0.4, 'Karton', 12, 'Trockengut', NULL, NULL, true, true, false, 620, false, 'Frau Schmidt', false, false, 'Keines', false, 'Offen', 'Niedrig', v_firma, NULL),
    ('A-9008', v_k4, 'GH-5561', v_depot, 'Nördlinger Straße 8, 91126 Schwabach', v_heute, v_morgen, '15:00', '16:00', '08:00', '10:00',
     3800, 1.2, 1.0, 1.2, 'Gitterbox', 6, 'Trockengut', NULL, NULL, true, false, true, 4300, true, 'Herr Huber', true, true, 'Stapler vor Ort', false, 'Offen', 'Normal', v_firma, NULL),
    -- unverplant, morgen
    ('A-9009', v_k1, 'KD-4830', v_depot, 'Schwabacher Straße 120, 90763 Fürth', v_morgen, v_morgen, '06:00', '07:00', '09:00', '09:30',
     2000, 1.2, 0.8, 1.6, 'Europalette', 5, 'Frische', 2, 7, false, false, false, 9900, true, 'Frau Meier', true, true, 'Hebebühne', false, 'Offen', 'Hoch', v_firma, NULL),
    ('A-9010', v_k3, 'BS-2215', v_depot, 'Hauptstraße 40, 91054 Erlangen', v_morgen, v_morgen, '06:00', '07:00', '11:00', '12:00',
     400, 1.2, 0.8, 1.1, 'Europalette', 2, 'Trockengut', NULL, NULL, true, false, false, 1300, false, 'Frau Schmidt', false, false, 'Keines', false, 'Offen', 'Normal', v_firma, NULL);

    -- ── Stopps ──────────────────────────────────────────────────────────────────────────
    -- T-9001: Depot erledigt, Fürth verspätet (ETA nach Fensterende), dann Erlangen.
    INSERT INTO public.tourstopp (tour_id, reihenfolge, art, bezeichnung, adresse, breitengrad, laengengrad,
        zeitfenster_von, zeitfenster_bis, geplante_ankunft, ist_ankunft, ist_abfahrt, status, aktuelle_eta, eta_berechnet_am)
    VALUES (v_t1, 1, 'Abholung', 'Depot Nürnberg Hafen', v_depot, 49.4106, 11.0487,
            v_jetzt - interval '3 hours', v_jetzt - interval '2 hours', v_jetzt - interval '150 minutes',
            v_jetzt - interval '150 minutes', v_jetzt - interval '125 minutes', 'Erledigt', NULL, NULL)
    RETURNING stopp_id INTO v_s;
    INSERT INTO public.stopp_auftrag (stopp_id, auftrag_id, vorgang_art)
    SELECT v_s, auftrag_id, 'Abholung' FROM public.auftrag WHERE unternehmen_id = v_firma AND auftragsnummer IN ('A-9001', 'A-9002');

    INSERT INTO public.tourstopp (tour_id, reihenfolge, art, bezeichnung, adresse, breitengrad, laengengrad,
        zeitfenster_von, zeitfenster_bis, geplante_ankunft, status, aktuelle_eta, eta_berechnet_am)
    VALUES (v_t1, 2, 'Zustellung', 'Fürth', 'Schwabacher Straße 120, 90763 Fürth', 49.4643, 10.9757,
            v_jetzt + interval '10 minutes', v_jetzt + interval '40 minutes', v_jetzt + interval '25 minutes',
            'Offen', v_jetzt + interval '60 minutes', v_jetzt)
    RETURNING stopp_id INTO v_s;
    INSERT INTO public.stopp_auftrag (stopp_id, auftrag_id, vorgang_art)
    SELECT v_s, auftrag_id, 'Zustellung' FROM public.auftrag WHERE unternehmen_id = v_firma AND auftragsnummer = 'A-9001';

    INSERT INTO public.tourstopp (tour_id, reihenfolge, art, bezeichnung, adresse, breitengrad, laengengrad,
        zeitfenster_von, zeitfenster_bis, geplante_ankunft, status, aktuelle_eta, eta_berechnet_am)
    VALUES (v_t1, 3, 'Zustellung', 'Erlangen', 'Hauptstraße 40, 91054 Erlangen', 49.5971, 11.0045,
            v_jetzt + interval '2 hours', v_jetzt + interval '3 hours', v_jetzt + interval '130 minutes',
            'Offen', v_jetzt + interval '150 minutes', v_jetzt)
    RETURNING stopp_id INTO v_s;
    INSERT INTO public.stopp_auftrag (stopp_id, auftrag_id, vorgang_art)
    SELECT v_s, auftrag_id, 'Zustellung' FROM public.auftrag WHERE unternehmen_id = v_firma AND auftragsnummer = 'A-9002';

    -- T-9002: Depot erledigt, Nürnberg Mitte pünktlich, dann Schwabach.
    INSERT INTO public.tourstopp (tour_id, reihenfolge, art, bezeichnung, adresse, breitengrad, laengengrad,
        zeitfenster_von, zeitfenster_bis, geplante_ankunft, ist_ankunft, ist_abfahrt, status)
    VALUES (v_t2, 1, 'Abholung', 'Depot Nürnberg Hafen', v_depot, 49.4106, 11.0487,
            v_jetzt - interval '2 hours', v_jetzt - interval '1 hour', v_jetzt - interval '100 minutes',
            v_jetzt - interval '100 minutes', v_jetzt - interval '80 minutes', 'Erledigt')
    RETURNING stopp_id INTO v_s;
    INSERT INTO public.stopp_auftrag (stopp_id, auftrag_id, vorgang_art)
    SELECT v_s, auftrag_id, 'Abholung' FROM public.auftrag WHERE unternehmen_id = v_firma AND auftragsnummer IN ('A-9003', 'A-9004');

    INSERT INTO public.tourstopp (tour_id, reihenfolge, art, bezeichnung, adresse, breitengrad, laengengrad,
        zeitfenster_von, zeitfenster_bis, geplante_ankunft, status, aktuelle_eta, eta_berechnet_am)
    VALUES (v_t2, 2, 'Zustellung', 'Nürnberg Mitte', 'Königstraße 15, 90402 Nürnberg', 49.4499, 11.0790,
            v_jetzt + interval '15 minutes', v_jetzt + interval '75 minutes', v_jetzt + interval '30 minutes',
            'Offen', v_jetzt + interval '35 minutes', v_jetzt)
    RETURNING stopp_id INTO v_s;
    INSERT INTO public.stopp_auftrag (stopp_id, auftrag_id, vorgang_art)
    SELECT v_s, auftrag_id, 'Zustellung' FROM public.auftrag WHERE unternehmen_id = v_firma AND auftragsnummer = 'A-9003';

    INSERT INTO public.tourstopp (tour_id, reihenfolge, art, bezeichnung, adresse, breitengrad, laengengrad,
        zeitfenster_von, zeitfenster_bis, geplante_ankunft, status, aktuelle_eta, eta_berechnet_am)
    VALUES (v_t2, 3, 'Zustellung', 'Schwabach', 'Nördlinger Straße 8, 91126 Schwabach', 49.3290, 11.0236,
            v_jetzt + interval '2 hours', v_jetzt + interval '4 hours', v_jetzt + interval '150 minutes',
            'Offen', v_jetzt + interval '155 minutes', v_jetzt)
    RETURNING stopp_id INTO v_s;
    INSERT INTO public.stopp_auftrag (stopp_id, auftrag_id, vorgang_art)
    SELECT v_s, auftrag_id, 'Zustellung' FROM public.auftrag WHERE unternehmen_id = v_firma AND auftragsnummer = 'A-9004';

    -- T-9003 und T-9004: eigene Stopps ohne Sendung (Depot und Kunde).
    INSERT INTO public.tourstopp (tour_id, reihenfolge, art, bezeichnung, adresse, breitengrad, laengengrad,
        zeitfenster_von, zeitfenster_bis, geplante_ankunft, status)
    VALUES
    (v_t3, 1, 'Abholung', 'Depot Nürnberg Hafen', v_depot, 49.4106, 11.0487,
     (v_heute + TIME '13:00') AT TIME ZONE 'Europe/Berlin', (v_heute + TIME '13:30') AT TIME ZONE 'Europe/Berlin',
     (v_heute + TIME '13:00') AT TIME ZONE 'Europe/Berlin', 'Offen'),
    (v_t3, 2, 'Zustellung', 'Zirndorf', 'Fürther Straße 30, 90513 Zirndorf', 49.4430, 10.9560,
     (v_heute + TIME '14:00') AT TIME ZONE 'Europe/Berlin', (v_heute + TIME '15:00') AT TIME ZONE 'Europe/Berlin',
     NULL, 'Offen'),
    (v_t4, 1, 'Abholung', 'Depot Nürnberg Hafen', v_depot, 49.4106, 11.0487,
     (v_morgen + TIME '06:30') AT TIME ZONE 'Europe/Berlin', (v_morgen + TIME '07:00') AT TIME ZONE 'Europe/Berlin',
     (v_morgen + TIME '06:30') AT TIME ZONE 'Europe/Berlin', 'Offen'),
    (v_t4, 2, 'Kombiniert', 'Leergut Fürth', 'Schwabacher Straße 120, 90763 Fürth', 49.4643, 10.9757,
     (v_morgen + TIME '09:00') AT TIME ZONE 'Europe/Berlin', (v_morgen + TIME '10:00') AT TIME ZONE 'Europe/Berlin',
     (v_morgen + TIME '09:15') AT TIME ZONE 'Europe/Berlin', 'Offen');

    RAISE NOTICE 'Demo-Daten für Firma % angelegt: 4 Fahrer, 4 Fahrzeuge, 4 Kunden, 10 Sendungen, 4 Touren.', v_kennung;
END $$;
