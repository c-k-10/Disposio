-- Testnachricht der Disposition an Nils (Phase 4, NICHT committen)
-- Ersetzt das Chatfenster der Software (kommt in Teil B): Sandra Kroher schreibt in den Chat von T-NILS-04.
-- Voraussetzung: Nils hat in der App bei T-NILS-04 einmal „Disposition kontaktieren“ geöffnet (legt den Chat an).
-- Mehrfach ausführbar (je Ausführung eine neue Nachricht). Status 'ausstehend': die API übersetzt beim Abruf.
-- Nur INSERT, eine Transaktion.

BEGIN;

INSERT INTO public.nachricht (nachrichtentext, zeitstempel, gelesen, originalsprache, zielsprache,
                              uebersetzungsstatus, uebertragungsstatus, chat_id, benutzer_id)
SELECT CASE d.sprache  -- Text in der Sprache der Disponentin (Sandra zurzeit en)
           WHEN 'en' THEN 'Please go to Bäckerei Ziegelstein first, the customer is already waiting.'
           WHEN 'pl' THEN 'Najpierw jedź do Bäckerei Ziegelstein, klient już czeka.'
           ELSE 'Bitte zuerst zur Bäckerei Ziegelstein fahren, der Kunde wartet schon.'
       END,
       now(), false, d.sprache, f.sprache,
       CASE WHEN d.sprache = f.sprache THEN 'nicht_noetig' ELSE 'ausstehend' END,
       'gesendet', c.chat_id, c.disponent_benutzer_id
FROM public.chat c
JOIN public.tour t ON t.tour_id = c.tour_id AND t.tournummer = 'T-NILS-04'
JOIN public.benutzer d ON d.benutzer_id = c.disponent_benutzer_id
JOIN public.benutzer f ON f.benutzer_id = c.fahrer_benutzer_id
WHERE lower(f.email) = 'nils@pferd.de';

COMMIT;
