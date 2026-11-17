-- Testdaten Fahrer-App Push-Benachrichtigungen, Teil 1 (NICHT committen, steht in .gitignore)
-- Firma Geis (Kennung Geis1), Disponentin Sandra Kroher (sandra@kroher.de), Fahrzeug AN-PL 961.
-- 1 Tour T-NILS-06 morgen 08:00 bis 12:00 (Berlin), Status Geplant, OHNE Fahrer, 1 Stopp.
-- Zuweisen an Nils danach mit testdaten_push_zuweisen.sql (Test 3).
-- Nur INSERTs, eine Transaktion. Bei einem Fehler wird nichts gespeichert.

BEGIN;

INSERT INTO public.tour (tournummer, startzeit, endzeit, startort, zielort, status,
                         unternehmen_id, abteilung_id, disponent_benutzer_id, fahrer_id, fahrzeug_id)
SELECT 'T-NILS-06',
       (date_trunc('day', now() AT TIME ZONE 'Europe/Berlin') + interval '1 day 8 hours') AT TIME ZONE 'Europe/Berlin',
       (date_trunc('day', now() AT TIME ZONE 'Europe/Berlin') + interval '1 day 12 hours') AT TIME ZONE 'Europe/Berlin',
       'Depot Nürnberg Hafen', 'Fürth Mitte', 'Geplant',
       u.unternehmen_id, a.abteilung_id, d.benutzer_id, NULL, fz.fahrzeug_id
FROM public.unternehmen u
JOIN public.abteilung a ON a.unternehmen_id = u.unternehmen_id AND a.bezeichnung = 'Hauptabteilung'
JOIN public.benutzer d ON d.unternehmen_id = u.unternehmen_id AND lower(d.email) = 'sandra@kroher.de'
JOIN public.fahrzeug fz ON fz.unternehmen_id = u.unternehmen_id AND fz.kennzeichen = 'AN-PL 961'
WHERE u.kennung = 'Geis1';

INSERT INTO public.tourstopp (tour_id, reihenfolge, art, bezeichnung, adresse, breitengrad, laengengrad,
                              zeitfenster_von, zeitfenster_bis, geplante_ankunft, status)
SELECT t.tour_id, 1, 'Zustellung', 'Getränkemarkt Fürth Mitte', 'Königswarterstraße 20, 90762 Fürth',
       49.472800, 10.990100,
       t.startzeit + interval '1 hour', t.startzeit + interval '3 hours', t.startzeit + interval '1 hour',
       'Offen'
FROM public.tour t
JOIN public.unternehmen u ON u.unternehmen_id = t.unternehmen_id AND u.kennung = 'Geis1'
WHERE t.tournummer = 'T-NILS-06';

COMMIT;

-- Kontrolle (soll 1 | 1 | true zeigen):
-- SELECT (SELECT count(*) FROM public.tour t JOIN public.unternehmen u USING (unternehmen_id)
--           WHERE u.kennung = 'Geis1' AND t.tournummer = 'T-NILS-06'),
--        (SELECT count(*) FROM public.tourstopp s JOIN public.tour t USING (tour_id) WHERE t.tournummer = 'T-NILS-06'),
--        (SELECT fahrer_id IS NULL FROM public.tour WHERE tournummer = 'T-NILS-06');
