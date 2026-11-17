-- Testdaten Fahrer-App Phase 2 (NICHT committen)
-- Firma Geis (Kennung Geis1), Fahrer nils@pferd.de
-- 1 Tour fuer HEUTE (Tag der Ausfuehrung), 3 Stopps (1 Abholung, 2 Zustellungen), 3 Auftraege.
-- Nutzt vorhandenes Fahrzeug AN-PL 961, Abteilung "Hauptabteilung" und die Kunden K-9001 / K-9002.
-- Nur INSERTs, eine Transaktion. Bei einem Fehler wird nichts gespeichert.
-- Bitte am Testtag ausfuehren: die Tour gilt fuer das heutige Datum.

BEGIN;

-- Tour T-NILS-01, Status wie in der Software: Fahrer und Fahrzeug zugewiesen
INSERT INTO public.tour (tournummer, startzeit, endzeit, startort, zielort, status,
                         unternehmen_id, abteilung_id, fahrer_id, fahrzeug_id)
SELECT 'T-NILS-01',
       ((now() AT TIME ZONE 'Europe/Berlin')::date + time '07:00') AT TIME ZONE 'Europe/Berlin',
       ((now() AT TIME ZONE 'Europe/Berlin')::date + time '15:00') AT TIME ZONE 'Europe/Berlin',
       'Depot Nürnberg Hafen', 'Fürth', 'Zugewiesen',
       u.unternehmen_id, a.abteilung_id, f.fahrer_id, fz.fahrzeug_id
FROM public.unternehmen u
JOIN public.abteilung a ON a.unternehmen_id = u.unternehmen_id AND a.bezeichnung = 'Hauptabteilung'
JOIN public.benutzer b ON b.unternehmen_id = u.unternehmen_id AND lower(b.email) = 'nils@pferd.de'
JOIN public.fahrer f ON f.benutzer_id = b.benutzer_id
JOIN public.fahrzeug fz ON fz.unternehmen_id = u.unternehmen_id AND fz.kennzeichen = 'AN-PL 961'
WHERE u.kennung = 'Geis1';

-- 3 Stopps in Reihenfolge
INSERT INTO public.tourstopp (tour_id, reihenfolge, art, bezeichnung, adresse, breitengrad, laengengrad,
                              zeitfenster_von, zeitfenster_bis, geplante_ankunft, status)
SELECT t.tour_id, s.reihenfolge, s.art, s.bezeichnung, s.adresse, s.breite, s.laenge,
       ((now() AT TIME ZONE 'Europe/Berlin')::date + s.von) AT TIME ZONE 'Europe/Berlin',
       ((now() AT TIME ZONE 'Europe/Berlin')::date + s.bis) AT TIME ZONE 'Europe/Berlin',
       ((now() AT TIME ZONE 'Europe/Berlin')::date + s.von) AT TIME ZONE 'Europe/Berlin',
       'Offen'
FROM public.tour t
JOIN public.unternehmen u ON u.unternehmen_id = t.unternehmen_id AND u.kennung = 'Geis1'
CROSS JOIN (VALUES
    (1, 'Abholung',   'Depot Nürnberg Hafen', 'Hafenstraße 25, 90451 Nürnberg',      49.410600, 11.048700, time '07:00', time '08:00'),
    (2, 'Zustellung', 'Bistro Mitte',         'Königstraße 15, 90402 Nürnberg',      49.449900, 11.079000, time '09:00', time '11:00'),
    (3, 'Zustellung', 'Markt Nord Fürth',     'Schwabacher Straße 120, 90763 Fürth', 49.464300, 10.975700, time '11:30', time '13:00')
) AS s(reihenfolge, art, bezeichnung, adresse, breite, laenge, von, bis)
WHERE t.tournummer = 'T-NILS-01';

-- 3 Auftraege, der Tour zugeordnet
INSERT INTO public.auftrag (auftragsnummer, kunde_id, kundenreferenz, abholadresse, lieferadresse,
                            abholdatum, lieferdatum, abholfenster_von, abholfenster_bis,
                            lieferfenster_von, lieferfenster_bis, gewicht_kg, laenge_m, breite_m, hoehe_m,
                            verpackungstyp, anzahl, gutart, temperatur_min_c, temperatur_max_c,
                            stapelbar, drehbar, gefahrgut, warenwert, versicherung, ansprechpartner,
                            avisierung_erforderlich, ladehilfsmitteltausch, lade_entladeequipment, zollrelevanz,
                            status, prioritaet, unternehmen_id, tour_id)
SELECT x.nr, k.kunde_id, x.referenz, 'Hafenstraße 25, 90451 Nürnberg', x.lieferadresse,
       (now() AT TIME ZONE 'Europe/Berlin')::date, (now() AT TIME ZONE 'Europe/Berlin')::date, time '07:00', time '08:00', x.von, x.bis,
       x.gewicht, 1.20, 0.80, x.hoehe,
       x.verpackung, x.anzahl, x.gutart, x.tmin, x.tmax,
       x.stapelbar, false, x.gefahrgut, x.wert, true, x.ansprechpartner,
       x.avis, true, x.equipment, false,
       'Verplant', x.prio, t.unternehmen_id, t.tour_id
FROM public.tour t
JOIN public.unternehmen u ON u.unternehmen_id = t.unternehmen_id AND u.kennung = 'Geis1'
CROSS JOIN (VALUES
    ('A-NILS-01', 'K-9002', 'BM-1101', 'Königstraße 15, 90402 Nürnberg',      time '09:00', time '11:00',
     640.00, 1.40, 'Europalette', 2, 'Frische', 2.0, 7.0, false, false, 1800.00,
     'Herr Yilmaz, 0911 223344', true, 'Hubwagen', 'Hoch'),
    ('A-NILS-02', 'K-9002', 'BM-1102', 'Königstraße 15, 90402 Nürnberg',      time '09:00', time '11:00',
     150.00, 1.00, 'Gitterbox', 1, 'Trockengut', NULL, NULL, true, false, 450.00,
     'Herr Yilmaz, 0911 223344', false, 'Keines', 'Normal'),
    ('A-NILS-03', 'K-9001', 'MN-2201', 'Schwabacher Straße 120, 90763 Fürth', time '11:30', time '13:00',
     1200.00, 1.60, 'Europalette', 4, 'Trockengut', NULL, NULL, true, true, 3900.00,
     'Frau Meier, 0911 700100', false, 'Hebebühne', 'Normal')
) AS x(nr, kundennummer, referenz, lieferadresse, von, bis, gewicht, hoehe, verpackung, anzahl, gutart,
       tmin, tmax, stapelbar, gefahrgut, wert, ansprechpartner, avis, equipment, prio)
JOIN public.kunde k ON k.unternehmen_id = u.unternehmen_id AND k.kundennummer = x.kundennummer
WHERE t.tournummer = 'T-NILS-01';

-- Stopp <-> Auftrag: alle 3 am Depot abholen, A-NILS-01/02 bei Bistro Mitte, A-NILS-03 in Fürth zustellen
INSERT INTO public.stopp_auftrag (stopp_id, auftrag_id, vorgang_art)
SELECT s.stopp_id, a.auftrag_id, v.vorgang
FROM public.tour t
JOIN public.unternehmen u ON u.unternehmen_id = t.unternehmen_id AND u.kennung = 'Geis1'
CROSS JOIN (VALUES
    (1, 'A-NILS-01', 'Abholung'),
    (1, 'A-NILS-02', 'Abholung'),
    (1, 'A-NILS-03', 'Abholung'),
    (2, 'A-NILS-01', 'Zustellung'),
    (2, 'A-NILS-02', 'Zustellung'),
    (3, 'A-NILS-03', 'Zustellung')
) AS v(reihenfolge, auftragsnummer, vorgang)
JOIN public.tourstopp s ON s.tour_id = t.tour_id AND s.reihenfolge = v.reihenfolge
JOIN public.auftrag a ON a.unternehmen_id = t.unternehmen_id AND a.auftragsnummer = v.auftragsnummer
WHERE t.tournummer = 'T-NILS-01';

COMMIT;

-- Kontrolle (soll 1 | 3 | 3 | 6 zeigen):
-- SELECT (SELECT count(*) FROM public.tour WHERE tournummer = 'T-NILS-01'),
--        (SELECT count(*) FROM public.tourstopp s JOIN public.tour t USING (tour_id) WHERE t.tournummer = 'T-NILS-01'),
--        (SELECT count(*) FROM public.auftrag WHERE auftragsnummer LIKE 'A-NILS-%'),
--        (SELECT count(*) FROM public.stopp_auftrag sa JOIN public.tourstopp s USING (stopp_id)
--           JOIN public.tour t USING (tour_id) WHERE t.tournummer = 'T-NILS-01');
