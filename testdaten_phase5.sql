-- Testdaten Fahrer-App Phase 5: Ablieferung, Tanken, Schaden (NICHT committen)
-- Firma Geis (Kennung Geis1), Fahrer nils@pferd.de, Fahrzeug AN-PL 961 (wie T-NILS-04),
-- Disponentin Sandra Kroher (sandra@kroher.de).
-- 1 Tour T-NILS-05 ab JETZT, Status Zugewiesen, 2 Stopps mit je 1 Zustellauftrag (Anzahl 10).
-- Vorher in der App T-NILS-04 abschließen (sie ist noch "Unterwegs", sonst lässt sich T-NILS-05 nicht starten).
-- Nur INSERTs, eine Transaktion. Bei einem Fehler wird nichts gespeichert.

BEGIN;

INSERT INTO public.tour (tournummer, startzeit, endzeit, startort, zielort, status,
                         unternehmen_id, abteilung_id, disponent_benutzer_id, fahrer_id, fahrzeug_id)
SELECT 'T-NILS-05',
       date_trunc('minute', now()) - interval '10 minutes',
       date_trunc('minute', now()) + interval '4 hours',
       'Depot Nürnberg Hafen', 'Nürnberg Süd', 'Zugewiesen',
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
    (1, 'Getränke Huber Südstadt', 'Allersberger Straße 120, 90461 Nürnberg', 49.437000, 11.090500, interval '0 minutes',  interval '2 hours'),
    (2, 'Bäckerei Sonne Langwasser', 'Breslauer Straße 300, 90471 Nürnberg',  49.411500, 11.135000, interval '30 minutes', interval '3 hours')
) AS s(reihenfolge, bezeichnung, adresse, breite, laenge, von, bis)
WHERE t.tournummer = 'T-NILS-05';

-- 2 Aufträge à 10 Europaletten, der Tour zugeordnet (Kunden wie in Phase 2)
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
       'Europalette', 10, 'Trockengut', NULL, NULL,
       true, false, false, x.wert, true, x.ansprechpartner,
       false, true, 'Hubwagen', false,
       'Verplant', 'Normal', t.unternehmen_id, t.tour_id
FROM public.tour t
JOIN public.unternehmen u ON u.unternehmen_id = t.unternehmen_id AND u.kennung = 'Geis1'
CROSS JOIN (VALUES
    ('A-NILS-51', 'K-9004', 'GH-5101', 'Allersberger Straße 120, 90461 Nürnberg', 2400.00, 3000.00, 'Herr Huber, 0911 445566'),
    ('A-NILS-52', 'K-9003', 'BS-5201', 'Breslauer Straße 300, 90471 Nürnberg',    1800.00, 2200.00, 'Frau Sonne, 0911 889900')
) AS x(nr, kundennummer, referenz, lieferadresse, gewicht, wert, ansprechpartner)
JOIN public.kunde k ON k.unternehmen_id = u.unternehmen_id AND k.kundennummer = x.kundennummer
WHERE t.tournummer = 'T-NILS-05';

-- Stopp <-> Auftrag: je Stopp ein Zustellauftrag
INSERT INTO public.stopp_auftrag (stopp_id, auftrag_id, vorgang_art)
SELECT s.stopp_id, a.auftrag_id, 'Zustellung'
FROM public.tour t
JOIN public.unternehmen u ON u.unternehmen_id = t.unternehmen_id AND u.kennung = 'Geis1'
CROSS JOIN (VALUES
    (1, 'A-NILS-51'),
    (2, 'A-NILS-52')
) AS v(reihenfolge, auftragsnummer)
JOIN public.tourstopp s ON s.tour_id = t.tour_id AND s.reihenfolge = v.reihenfolge
JOIN public.auftrag a ON a.unternehmen_id = t.unternehmen_id AND a.auftragsnummer = v.auftragsnummer
WHERE t.tournummer = 'T-NILS-05';

COMMIT;

-- Kontrolle (soll 1 | 2 | 2 zeigen):
-- SELECT (SELECT count(*) FROM public.tour WHERE tournummer = 'T-NILS-05'),
--        (SELECT count(*) FROM public.tourstopp s JOIN public.tour t USING (tour_id) WHERE t.tournummer = 'T-NILS-05'),
--        (SELECT count(*) FROM public.stopp_auftrag sa JOIN public.tourstopp s USING (stopp_id)
--           JOIN public.tour t USING (tour_id) WHERE t.tournummer = 'T-NILS-05');

-- Nach dem Test in der App:
-- SELECT * FROM public.abliefernachweis ORDER BY nachweis_id DESC LIMIT 5;
-- SELECT dokument_id, dokumentart, dateireferenz, nachweis_id, tankvorgang_id, schaden_id FROM public.dokument ORDER BY dokument_id DESC LIMIT 10;
-- SELECT * FROM public.tankvorgang ORDER BY tankvorgang_id DESC LIMIT 3;
-- SELECT * FROM public.kilometerstand ORDER BY kilometerstand_id DESC LIMIT 3;
-- SELECT * FROM public.schadensmeldung ORDER BY schaden_id DESC LIMIT 3;
