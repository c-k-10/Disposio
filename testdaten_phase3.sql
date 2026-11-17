-- Testdaten Fahrer-App Phase 3, Test Live-Karte der Software (NICHT committen)
-- Firma Geis (Kennung Geis1), Fahrer nils@pferd.de, Fahrzeug AN-PL 961 (wie T-NILS-01)
-- 1 Tour T-NILS-03 ab JETZT, 3 Zustellungen entlang der Strecke Nürnberg Hafen -> Fürth Hauptbahnhof.
-- Emulator-Route dazu: Extended Controls -> Location -> Routes,
--   Start "Hafenstraße 25, Nürnberg", Ziel "Bahnhofplatz 1, Fürth".
-- Zeitfenster relativ zur Ausführung: Stopp 3 endet 5 Minuten nach Ausführung -> wird "verspätet".
-- Also kurz vor dem Test ausführen.
-- Nur INSERTs, eine Transaktion. Bei einem Fehler wird nichts gespeichert.

BEGIN;

-- Tour T-NILS-03, Anfangsstatus wie T-NILS-01: Fahrer und Fahrzeug zugewiesen
INSERT INTO public.tour (tournummer, startzeit, endzeit, startort, zielort, status,
                         unternehmen_id, abteilung_id, fahrer_id, fahrzeug_id)
SELECT 'T-NILS-03',
       date_trunc('minute', now()) - interval '10 minutes',
       date_trunc('minute', now()) + interval '4 hours',
       'Depot Nürnberg Hafen', 'Fürth', 'Zugewiesen',
       u.unternehmen_id, a.abteilung_id, f.fahrer_id, fz.fahrzeug_id
FROM public.unternehmen u
JOIN public.abteilung a ON a.unternehmen_id = u.unternehmen_id AND a.bezeichnung = 'Hauptabteilung'
JOIN public.benutzer b ON b.unternehmen_id = u.unternehmen_id AND lower(b.email) = 'nils@pferd.de'
JOIN public.fahrer f ON f.benutzer_id = b.benutzer_id
JOIN public.fahrzeug fz ON fz.unternehmen_id = u.unternehmen_id AND fz.kennzeichen = 'AN-PL 961'
WHERE u.kennung = 'Geis1';

-- 3 Stopps in Reihenfolge (Koordinaten auf bzw. nahe der Strecke)
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
    (1, 'Bistro Plärrer',      'Plärrer 2, 90429 Nürnberg',          49.448500, 11.066200, interval '0 minutes',  interval '2 hours'),
    (2, 'Markt Muggenhof',     'Fürther Straße 250, 90429 Nürnberg', 49.458000, 11.035000, interval '0 minutes',  interval '3 hours'),
    -- Zeitfenster endet gleich nach Ausführung: ETA liegt danach -> verspätet
    (3, 'Kiosk Fürth Hbf',     'Bahnhofplatz 1, 90762 Fürth',        49.470000, 10.989500, interval '-1 hour',    interval '5 minutes')
) AS s(reihenfolge, bezeichnung, adresse, breite, laenge, von, bis)
WHERE t.tournummer = 'T-NILS-03';

-- 3 Aufträge, der Tour zugeordnet (Kunden wie in Phase 2)
INSERT INTO public.auftrag (auftragsnummer, kunde_id, kundenreferenz, abholadresse, lieferadresse,
                            abholdatum, lieferdatum, abholfenster_von, abholfenster_bis,
                            lieferfenster_von, lieferfenster_bis, gewicht_kg, laenge_m, breite_m, hoehe_m,
                            verpackungstyp, anzahl, gutart, temperatur_min_c, temperatur_max_c,
                            stapelbar, drehbar, gefahrgut, warenwert, versicherung, ansprechpartner,
                            avisierung_erforderlich, ladehilfsmitteltausch, lade_entladeequipment, zollrelevanz,
                            status, prioritaet, unternehmen_id, tour_id)
SELECT x.nr, k.kunde_id, x.referenz, 'Hafenstraße 25, 90451 Nürnberg', x.lieferadresse,
       (now() AT TIME ZONE 'Europe/Berlin')::date, (now() AT TIME ZONE 'Europe/Berlin')::date,
       time '06:00', time '07:00', time '08:00', time '18:00',
       x.gewicht, 1.20, 0.80, 1.20,
       'Europalette', x.anzahl, 'Trockengut', NULL, NULL,
       true, false, false, x.wert, true, x.ansprechpartner,
       false, true, 'Hubwagen', false,
       'Verplant', 'Normal', t.unternehmen_id, t.tour_id
FROM public.tour t
JOIN public.unternehmen u ON u.unternehmen_id = t.unternehmen_id AND u.kennung = 'Geis1'
CROSS JOIN (VALUES
    ('A-NILS-21', 'K-9002', 'BM-1301', 'Plärrer 2, 90429 Nürnberg',          300.00, 1, 600.00,  'Herr Yilmaz, 0911 223344'),
    ('A-NILS-22', 'K-9001', 'MN-2401', 'Fürther Straße 250, 90429 Nürnberg', 900.00, 3, 2100.00, 'Frau Meier, 0911 700100'),
    ('A-NILS-23', 'K-9001', 'MN-2402', 'Bahnhofplatz 1, 90762 Fürth',        450.00, 2, 950.00,  'Frau Meier, 0911 700100')
) AS x(nr, kundennummer, referenz, lieferadresse, gewicht, anzahl, wert, ansprechpartner)
JOIN public.kunde k ON k.unternehmen_id = u.unternehmen_id AND k.kundennummer = x.kundennummer
WHERE t.tournummer = 'T-NILS-03';

-- Stopp <-> Auftrag: je Stopp ein Auftrag
INSERT INTO public.stopp_auftrag (stopp_id, auftrag_id, vorgang_art)
SELECT s.stopp_id, a.auftrag_id, 'Zustellung'
FROM public.tour t
JOIN public.unternehmen u ON u.unternehmen_id = t.unternehmen_id AND u.kennung = 'Geis1'
CROSS JOIN (VALUES
    (1, 'A-NILS-21'),
    (2, 'A-NILS-22'),
    (3, 'A-NILS-23')
) AS v(reihenfolge, auftragsnummer)
JOIN public.tourstopp s ON s.tour_id = t.tour_id AND s.reihenfolge = v.reihenfolge
JOIN public.auftrag a ON a.unternehmen_id = t.unternehmen_id AND a.auftragsnummer = v.auftragsnummer
WHERE t.tournummer = 'T-NILS-03';

COMMIT;

-- Kontrolle (soll 1 | 3 | 3 zeigen):
-- SELECT (SELECT count(*) FROM public.tour WHERE tournummer = 'T-NILS-03'),
--        (SELECT count(*) FROM public.tourstopp s JOIN public.tour t USING (tour_id) WHERE t.tournummer = 'T-NILS-03'),
--        (SELECT count(*) FROM public.stopp_auftrag sa JOIN public.tourstopp s USING (stopp_id)
--           JOIN public.tour t USING (tour_id) WHERE t.tournummer = 'T-NILS-03');

-- Während des Tests:
-- SELECT erfasst_am, empfangen_am, breitengrad, laengengrad, genauigkeit_m FROM public.gps_position p
--   JOIN public.tour t USING (tour_id) WHERE t.tournummer = 'T-NILS-03' ORDER BY erfasst_am DESC LIMIT 10;
-- SELECT reihenfolge, bezeichnung, zeitfenster_bis, aktuelle_eta, eta_berechnet_am FROM public.tourstopp s
--   JOIN public.tour t USING (tour_id) WHERE t.tournummer = 'T-NILS-03' ORDER BY reihenfolge;
