-- Testdaten Fahrer-App Push-Benachrichtigungen, Teil 2 (NICHT committen, steht in .gitignore)
-- Weist T-NILS-06 (Firma Geis1) dem Fahrer nils@pferd.de zu, wie D06 in der Software:
-- fahrer_id setzen, Status Zugewiesen (Fahrzeug ist schon gesetzt).
-- Spaetestens ca. 20 s danach sendet die API den Push "Neue Tour T-NILS-06".
-- Eine Transaktion, betrifft genau eine Zeile.

BEGIN;

UPDATE public.tour t
SET fahrer_id = f.fahrer_id,
    status = 'Zugewiesen'
FROM public.unternehmen u
JOIN public.benutzer b ON b.unternehmen_id = u.unternehmen_id AND lower(b.email) = 'nils@pferd.de'
JOIN public.fahrer f ON f.benutzer_id = b.benutzer_id
WHERE u.kennung = 'Geis1'
  AND t.unternehmen_id = u.unternehmen_id
  AND t.tournummer = 'T-NILS-06'
  AND t.fahrer_id IS NULL;

COMMIT;

-- Kontrolle (soll Zugewiesen | true zeigen):
-- SELECT t.status, t.fahrer_id IS NOT NULL FROM public.tour t WHERE t.tournummer = 'T-NILS-06';
