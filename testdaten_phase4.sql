-- Testdaten Fahrer-App Phase 4: Chat mit der Disposition (NICHT committen)
-- Firma Geis (Kennung Geis1), Fahrer nils@pferd.de, Fahrzeug AN-PL 961 (wie T-NILS-02)
-- 1 Tour T-NILS-04 ab JETZT, Status Zugewiesen, 2 Zustellungen in Nürnberg.
-- Disponentin: Sandra Kroher (sandra@kroher.de), einziger Disponent der Firma Geis (Sprache zurzeit en).
--   Nils schreibt Deutsch -> Sandra bekommt Englisch (mit DeepL-Schlüssel).
-- Nur INSERTs, eine Transaktion. Bei einem Fehler wird nichts gespeichert.

BEGIN;

INSERT INTO public.tour (tournummer, startzeit, endzeit, startort, zielort, status,
                         unternehmen_id, abteilung_id, disponent_benutzer_id, fahrer_id, fahrzeug_id)
SELECT 'T-NILS-04',
       date_trunc('minute', now()) - interval '10 minutes',
       date_trunc('minute', now()) + interval '4 hours',
       'Depot Nürnberg Hafen', 'Nürnberg Nordost', 'Zugewiesen',
       u.unternehmen_id, a.abteilung_id, d.benutzer_id, f.fahrer_id, fz.fahrzeug_id
FROM public.unternehmen u
JOIN public.abteilung a ON a.unternehmen_id = u.unternehmen_id AND a.bezeichnung = 'Hauptabteilung'
JOIN public.benutzer b ON b.unternehmen_id = u.unternehmen_id AND lower(b.email) = 'nils@pferd.de'
JOIN public.fahrer f ON f.benutzer_id = b.benutzer_id
JOIN public.benutzer d ON d.unternehmen_id = u.unternehmen_id AND lower(d.email) = 'sandra@kroher.de'
JOIN public.fahrzeug fz ON fz.unternehmen_id = u.unternehmen_id AND fz.kennzeichen = 'AN-PL 961'
WHERE u.kennung = 'Geis1';

INSERT INTO public.tourstopp (tour_id, reihenfolge, art, bezeichnung, adresse, breitengrad, laengengrad,
                              zeitfenster_von, zeitfenster_bis, geplante_ankunft, status)
SELECT t.tour_id, s.reihenfolge, 'Zustellung', s.bezeichnung, s.adresse, s.breite, s.laenge,
       date_trunc('minute', now()) + s.von,
       date_trunc('minute', now()) + s.bis,
       date_trunc('minute', now()) + s.von,
       'Offen'
FROM public.tour t
JOIN public.unternehmen u ON u.unternehmen_id = t.unternehmen_id AND u.kennung = 'Geis1'
CROSS JOIN (VALUES
    (1, 'Getränke Wöhrd',   'Wöhrder Hauptstraße 10, 90489 Nürnberg', 49.454800, 11.093600, interval '0 minutes', interval '2 hours'),
    (2, 'Bäckerei Ziegelstein', 'Ziegelsteinstraße 100, 90411 Nürnberg', 49.483900, 11.103700, interval '30 minutes', interval '3 hours')
) AS s(reihenfolge, bezeichnung, adresse, breite, laenge, von, bis)
WHERE t.tournummer = 'T-NILS-04';

COMMIT;

-- Kontrolle: 1 Zeile, 2 Stopps, Disponent Sandra Kroher
SELECT t.tournummer, t.status, t.startzeit, d.vorname || ' ' || d.nachname AS disponent,
       (SELECT count(*) FROM public.tourstopp s WHERE s.tour_id = t.tour_id) AS stopps
FROM public.tour t
JOIN public.benutzer d ON d.benutzer_id = t.disponent_benutzer_id
WHERE t.tournummer = 'T-NILS-04';
