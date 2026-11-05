-- Demo: drei Mandanten mit typischen Fehlern, damit der „Systemzustand“ im Admin-Dashboard etwas anzeigt.
-- NUR für die Entwicklungs-/Testdatenbank gedacht. Entfernen mit 013_demo_fehlerfirmen_entfernen.sql.
--
-- So geht's: Das ganze Skript im Neon SQL Editor ausführen, danach im Dashboard „Jetzt prüfen“.
--
--   1. Demo Übersetzung Kaputt GmbH  → Kachel „Übersetzung“ ROT  (7 Chat-Nachrichten ohne Übersetzung)
--   2. Demo Ohne Admin AG            → Kachel „Kundenadmins“ GELB (aktive Firma ohne Kundenadmin)
--   3. Demo Webspam Logistik         → Kachel „Web-Anmeldungen“ ROT (31 Web-Sendungen in der letzten Stunde)
--
-- Hinweis zu 3: Die Prüfung zählt nur die letzte Stunde. Danach wird die Kachel wieder grün.
-- Einfach das Skript nochmal ausführen – es legt dann nur die Web-Anfragen neu an.
--
-- Erkennbar an: Kennungen demo-fehler-…, E-Mails …@demo.disposio.test, Tour T-DEMO-UE1.
-- Die Demo-Konten haben ein unbekanntes Zufallspasswort und können sich nicht anmelden.
-- Voraussetzung: Migrationen bis 011 ausgeführt.

DO $$
DECLARE
    v_hash      text := '$2a$11$M1fUFSAUMkF5deLMOrpUTuBQKeBUmb4SsnhCp/.9gI1qwXN9M0GQm'; -- Zufallspasswort, wie in 007
    v_admin     bigint;   -- rollen_id Kundenadmin
    v_fahrer_r  bigint;   -- rollen_id Fahrer
    v_firma     bigint;
    v_abt       bigint;
    v_dispo     bigint;   -- benutzer_id Kundenadmin (schreibt im Chat als Disponent)
    v_fb        bigint;   -- benutzer_id Fahrer
    v_f         bigint;   -- fahrer_id
    v_tour      bigint;
    v_chat      bigint;
    v_jetzt     timestamptz := now();
    i           int;
BEGIN
    SELECT rollen_id INTO v_admin FROM public.rolle WHERE bezeichnung = 'Kundenadmin' AND unternehmen_id IS NULL LIMIT 1;
    SELECT rollen_id INTO v_fahrer_r FROM public.rolle WHERE bezeichnung = 'Fahrer' AND unternehmen_id IS NULL LIMIT 1;
    IF v_admin IS NULL THEN
        RAISE EXCEPTION 'Die Standardrolle „Kundenadmin“ fehlt in public.rolle.';
    END IF;

    -- ── 1. Demo Übersetzung Kaputt GmbH ─────────────────────────────────────────────────
    IF NOT EXISTS (SELECT 1 FROM public.unternehmen WHERE kennung = 'demo-fehler-uebersetzung') THEN
        INSERT INTO public.unternehmen (firmenname, kennung, admin_email, status, adresse, plz, ort, telefon, email,
                                        kontaktperson, passwortregeln)
        VALUES ('Demo Übersetzung Kaputt GmbH', 'demo-fehler-uebersetzung', 'admin@uebersetzung.demo.disposio.test',
                'Aktiv', 'Hafenstraße 1', '90451', 'Nürnberg', '0911 000001', 'info@uebersetzung.demo.disposio.test',
                'Demo Admin', 'Mindestens 8 Zeichen')
        RETURNING unternehmen_id INTO v_firma;
        INSERT INTO public.abteilung (bezeichnung, unternehmen_id) VALUES ('Hauptabteilung', v_firma)
        RETURNING abteilung_id INTO v_abt;

        -- Kundenadmin (damit die Firma nur bei „Übersetzung“ auffällt)
        INSERT INTO public.benutzer (vorname, nachname, email, passwort_hash, unternehmen_id, abteilung_id, sprache, aktiv)
        VALUES ('Demo', 'Admin', 'admin@uebersetzung.demo.disposio.test', v_hash, v_firma, v_abt, 'de', true)
        RETURNING benutzer_id INTO v_dispo;
        INSERT INTO public.benutzer_rolle (benutzer_id, rollen_id) VALUES (v_dispo, v_admin);

        -- Polnischer Fahrer mit Tour heute
        INSERT INTO public.benutzer (vorname, nachname, email, passwort_hash, unternehmen_id, abteilung_id, sprache, aktiv)
        VALUES ('Tomasz', 'Wiśniewski', 'tomasz@uebersetzung.demo.disposio.test', v_hash, v_firma, v_abt, 'pl', true)
        RETURNING benutzer_id INTO v_fb;
        IF v_fahrer_r IS NOT NULL THEN INSERT INTO public.benutzer_rolle (benutzer_id, rollen_id) VALUES (v_fb, v_fahrer_r); END IF;
        INSERT INTO public.fahrer (fuehrerscheinnummer, fuehrerscheinklasse, eintrittsdatum, verfuegbarkeit, benutzer_id)
        VALUES ('DEMO-UE0001', 'CE', DATE '2020-01-01', 'Verfügbar', v_fb) RETURNING fahrer_id INTO v_f;

        INSERT INTO public.tour (tournummer, startzeit, endzeit, startort, zielort, status, unternehmen_id, abteilung_id, fahrer_id)
        VALUES ('T-DEMO-UE1', v_jetzt - interval '2 hours', v_jetzt + interval '6 hours',
                'Depot Nürnberg', 'Depot Nürnberg', 'Unterwegs', v_firma, v_abt, v_f)
        RETURNING tour_id INTO v_tour;

        INSERT INTO public.chat (status, tour_id, fahrer_benutzer_id, disponent_benutzer_id)
        VALUES ('offen', v_tour, v_fb, v_dispo) RETURNING chat_id INTO v_chat;

        -- 6× „fehlgeschlagen“ (DeepL nicht erreichbar) + 1× seit 20 Minuten „ausstehend“ (hängt)
        INSERT INTO public.nachricht (nachrichtentext, zeitstempel, originalsprache, zielsprache,
                                      uebersetzungsstatus, uebertragungsstatus, chat_id, benutzer_id)
        SELECT t.text, v_jetzt - (t.min || ' minutes')::interval, t.von, t.nach, t.status, 'gesendet', v_chat,
               CASE WHEN t.von = 'pl' THEN v_fb ELSE v_dispo END
        FROM (VALUES
            ('Dzień dobry, jestem w drodze.',                 90, 'pl', 'de', 'fehlgeschlagen'),
            ('Guten Morgen! Bitte melden Sie sich bei Ankunft.', 85, 'de', 'pl', 'fehlgeschlagen'),
            ('Na autostradzie jest korek.',                   70, 'pl', 'de', 'fehlgeschlagen'),
            ('Danke, wir informieren den Kunden.',            65, 'de', 'pl', 'fehlgeschlagen'),
            ('Jestem na miejscu, rampa jest zajęta.',         40, 'pl', 'de', 'fehlgeschlagen'),
            ('Bitte an Rampe 3 ausweichen.',                  35, 'de', 'pl', 'fehlgeschlagen'),
            ('Rozładunek zakończony.',                        20, 'pl', 'de', 'ausstehend')
        ) AS t(text, min, von, nach, status);

        RAISE NOTICE 'Angelegt: Demo Übersetzung Kaputt GmbH';
    END IF;

    -- ── 2. Demo Ohne Admin AG ───────────────────────────────────────────────────────────
    -- Aktive Firma ohne ein einziges Konto, also auch ohne Kundenadmin.
    IF NOT EXISTS (SELECT 1 FROM public.unternehmen WHERE kennung = 'demo-fehler-ohne-admin') THEN
        INSERT INTO public.unternehmen (firmenname, kennung, admin_email, status, adresse, plz, ort, telefon, email,
                                        kontaktperson, passwortregeln)
        VALUES ('Demo Ohne Admin AG', 'demo-fehler-ohne-admin', 'admin@ohneadmin.demo.disposio.test',
                'Aktiv', 'Bahnhofstraße 2', '91054', 'Erlangen', '09131 000002', 'info@ohneadmin.demo.disposio.test',
                'Niemand', 'Mindestens 8 Zeichen')
        RETURNING unternehmen_id INTO v_firma;
        INSERT INTO public.abteilung (bezeichnung, unternehmen_id) VALUES ('Hauptabteilung', v_firma);
        RAISE NOTICE 'Angelegt: Demo Ohne Admin AG';
    END IF;

    -- ── 3. Demo Webspam Logistik ────────────────────────────────────────────────────────
    SELECT unternehmen_id INTO v_firma FROM public.unternehmen WHERE kennung = 'demo-fehler-webspam';
    IF v_firma IS NULL THEN
        INSERT INTO public.unternehmen (firmenname, kennung, admin_email, status, adresse, plz, ort, telefon, email,
                                        kontaktperson, passwortregeln)
        VALUES ('Demo Webspam Logistik', 'demo-fehler-webspam', 'admin@webspam.demo.disposio.test',
                'Aktiv', 'Industriestraße 3', '90763', 'Fürth', '0911 000003', 'info@webspam.demo.disposio.test',
                'Demo Admin', 'Mindestens 8 Zeichen')
        RETURNING unternehmen_id INTO v_firma;
        INSERT INTO public.abteilung (bezeichnung, unternehmen_id) VALUES ('Hauptabteilung', v_firma)
        RETURNING abteilung_id INTO v_abt;
        INSERT INTO public.benutzer (vorname, nachname, email, passwort_hash, unternehmen_id, abteilung_id, sprache, aktiv)
        VALUES ('Demo', 'Admin', 'admin@webspam.demo.disposio.test', v_hash, v_firma, v_abt, 'de', true)
        RETURNING benutzer_id INTO v_dispo;
        INSERT INTO public.benutzer_rolle (benutzer_id, rollen_id) VALUES (v_dispo, v_admin);
        RAISE NOTICE 'Angelegt: Demo Webspam Logistik';
    END IF;

    -- Bei jedem Ausführen: 31 Web-Anfragen in der letzten Stunde, falls gerade weniger als 30 da sind.
    -- (Nur Protokolleinträge; es werden keine Sendungen angelegt.)
    IF (SELECT count(*) FROM public.aktionsprotokoll
        WHERE unternehmen_id = v_firma AND akteur_art = 'Webseite' AND zeitpunkt > now() - interval '1 hour') < 30 THEN
        FOR i IN 1..31 LOOP
            INSERT INTO public.aktionsprotokoll (zeitpunkt, akteur_art, aktion, ergebnis, detail, unternehmen_id)
            VALUES (v_jetzt - (i || ' minutes')::interval, 'Webseite', 'Sendung angefragt', 'Gespeichert',
                    'Demo-Spam ' || i || ' über die Webseite', v_firma);
        END LOOP;
        RAISE NOTICE 'Demo Webspam Logistik: 31 Web-Anfragen der letzten Stunde angelegt';
    END IF;
END $$;
