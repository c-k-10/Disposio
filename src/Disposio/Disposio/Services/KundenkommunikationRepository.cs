using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Mail;
using System.Threading;
using System.Threading.Tasks;
using Disposio.Models;

namespace Disposio.Services;

/// <summary>
/// D10 „Kundenkommunikation“: Verspätungsvorschläge von „Winston“ (public.winston_vorschlag) erzeugen, bearbeiten,
/// freigeben oder verwerfen. Winston ist hier eine feste Regel mit Textvorlage, keine KI:
/// Liegt die ETA eines offenen Zustell-Stopps mehr als 15 Minuten nach Fensterende, entsteht je Sendung ein Vorschlag.
/// Ohne Freigabe durch einen Disponenten wird nichts gesendet.
/// Kunden-E-Mail: Versand wird nur SIMULIERT (Aktionsprotokoll mit Empfänger und Text, Kanal „E-Mail“).
/// Fahrer: echte Chat-Nachricht in D09 bzw. der Fahrer-App.
/// </summary>
public sealed class KundenkommunikationRepository
{
    private const FirmenRolle Rolle = FirmenRolle.Disponent;
    public const int SchwelleMinuten = 15;

    private readonly Lazy<NeonSqlClient> _client = new(() => new NeonSqlClient());

    // Ein Befehl: Vorschläge ohne Verspätung auf „veraltet“ setzen, für neue Verspätungen Vorschläge anlegen.
    // Neu nur, wenn es für Stopp + Sendung keinen offenen gibt und sich die ETA seit dem letzten Vorschlag
    // um mehr als 15 Minuten verschlechtert hat (sonst käme nach jeder Entscheidung sofort ein neuer).
    // Gibt die Ids neuer Vorschläge zurück, deren Fahrertext noch übersetzt werden muss.
    private const string ErzeugenQuery = """
        WITH spaet AS (
            SELECT s.stopp_id, sa.auftrag_id, s.aktuelle_eta, s.zeitfenster_bis,
                   fb.benutzer_id AS fahrer_bid, fb.sprache, k.email,
                   to_char(s.aktuelle_eta AT TIME ZONE 'Europe/Berlin', 'HH24:MI') AS eta,
                   to_char(s.zeitfenster_bis AT TIME ZONE 'Europe/Berlin', 'HH24:MI') AS fenster,
                   ceil(extract(epoch FROM s.aktuelle_eta - s.zeitfenster_bis) / 60)::int AS minuten
            FROM public.tourstopp s
            JOIN public.tour t ON t.tour_id = s.tour_id
            JOIN public.stopp_auftrag sa ON sa.stopp_id = s.stopp_id AND sa.vorgang_art = 'Zustellung'
            JOIN public.auftrag a ON a.auftrag_id = sa.auftrag_id AND a.unternehmen_id = t.unternehmen_id
            JOIN public.kunde k ON k.kunde_id = a.kunde_id
            JOIN public.fahrer f ON f.fahrer_id = t.fahrer_id
            JOIN public.benutzer fb ON fb.benutzer_id = f.benutzer_id
            WHERE t.unternehmen_id = $1::bigint AND s.status <> 'Erledigt'
              AND s.aktuelle_eta IS NOT NULL AND s.zeitfenster_bis IS NOT NULL
              AND s.aktuelle_eta > s.zeitfenster_bis + interval '15 minutes'
              AND lower(t.status) NOT SIMILAR TO '%(abgeschlossen|beendet|storniert)%'
        ), veraltet AS (
            UPDATE public.winston_vorschlag v SET status = 'veraltet'
            FROM public.auftrag a
            WHERE a.auftrag_id = v.auftrag_id AND a.unternehmen_id = $1::bigint AND v.status = 'offen'
              AND NOT EXISTS (SELECT 1 FROM spaet sp WHERE sp.stopp_id = v.stopp_id AND sp.auftrag_id = v.auftrag_id)
            RETURNING v.vorschlag_id
        ), neu AS (
            INSERT INTO public.winston_vorschlag (stopp_id, auftrag_id, eta_snapshot, fensterende_snapshot, begruendung,
                status, textversion, kundentext, kundenempfaenger, kundensprache, fahrertext_original,
                fahrertext_uebersetzt, ausgangssprache, fahrersprache, fahrer_benutzer_id)
            SELECT sp.stopp_id, sp.auftrag_id, sp.aktuelle_eta, sp.zeitfenster_bis,
                   'ETA ' || sp.eta || ' liegt ' || sp.minuten || ' Minuten nach Ende des Lieferfensters (' || sp.fenster
                       || '). Vorschlag ab mehr als 15 Minuten.',
                   'offen', 1,
                   'Sehr geehrte Damen und Herren,' || E'\n\n'
                       || 'Ihre Lieferung wird voraussichtlich um ' || sp.eta || ' Uhr bei Ihnen eintreffen. '
                       || 'Wir bitten Sie, die Verspätung zu entschuldigen.' || E'\n\n'
                       || 'Vielen Dank für Ihr Verständnis.' || E'\n\n'
                       || 'Mit freundlichen Grüßen' || E'\n' || 'Ihr Dispositionsteam',
                   sp.email, 'de',
                   'Voraussichtliche Ankunft: ' || sp.eta || '. Bitte melden Sie sich bei Ankunft.',
                   CASE WHEN sp.sprache = 'de'
                        THEN 'Voraussichtliche Ankunft: ' || sp.eta || '. Bitte melden Sie sich bei Ankunft.' END,
                   'de', sp.sprache, sp.fahrer_bid
            FROM spaet sp
            WHERE NOT EXISTS (
                SELECT 1 FROM public.winston_vorschlag v
                WHERE v.stopp_id = sp.stopp_id AND v.auftrag_id = sp.auftrag_id
                  AND (v.status = 'offen' OR v.eta_snapshot >= sp.aktuelle_eta - interval '15 minutes'))
            RETURNING vorschlag_id, fahrertext_uebersetzt
        )
        SELECT vorschlag_id FROM neu WHERE fahrertext_uebersetzt IS NULL
        """;

    // Offene Vorschläge der Firma, älteste zuerst. Mit aktueller ETA, um eine Änderung seit dem Vorschlag zu zeigen.
    private const string OffeneQuery = """
        SELECT v.vorschlag_id, t.tour_id, t.tournummer, COALESCE(NULLIF(s.bezeichnung, ''), s.adresse, 'Stopp'),
               k.firmenname, a.auftragsnummer,
               to_char(v.eta_snapshot AT TIME ZONE 'Europe/Berlin', 'HH24:MI'),
               to_char(v.fensterende_snapshot AT TIME ZONE 'Europe/Berlin', 'HH24:MI'),
               v.begruendung, v.kundentext, v.kundenempfaenger, v.fahrertext_original, v.fahrertext_uebersetzt,
               v.fahrersprache, fb.vorname || ' ' || fb.nachname, v.textversion,
               to_char(COALESCE(s.eta_berechnet_am, v.erstellt_am) AT TIME ZONE 'Europe/Berlin', 'HH24:MI'),
               to_char(s.aktuelle_eta AT TIME ZONE 'Europe/Berlin', 'HH24:MI'),
               COALESCE(round(extract(epoch FROM s.aktuelle_eta - v.eta_snapshot) / 60)::int, 0)
        FROM public.winston_vorschlag v
        JOIN public.tourstopp s ON s.stopp_id = v.stopp_id
        JOIN public.tour t ON t.tour_id = s.tour_id
        JOIN public.auftrag a ON a.auftrag_id = v.auftrag_id
        JOIN public.kunde k ON k.kunde_id = a.kunde_id
        JOIN public.benutzer fb ON fb.benutzer_id = v.fahrer_benutzer_id
        WHERE t.unternehmen_id = $1::bigint AND v.status = 'offen'
        ORDER BY v.erstellt_am, v.vorschlag_id
        """;

    // Deutsche Fassung oder Sprache für den Fahrer geändert: speichern, Übersetzung leeren (die Neon Function
    // „uebersetzen“ füllt sie neu). Erhöht die Textversion. $1 Firma, $2 Vorschlag, $3 Text, $4 Sprache.
    private const string FahrertextQuery = """
        UPDATE public.winston_vorschlag v
        SET fahrertext_original = $3, fahrersprache = $4,
            fahrertext_uebersetzt = CASE WHEN $4 = v.ausgangssprache THEN $3 END,
            textversion = v.textversion + 1
        FROM public.tourstopp s JOIN public.tour t ON t.tour_id = s.tour_id
        WHERE v.vorschlag_id = $2::bigint AND s.stopp_id = v.stopp_id AND t.unternehmen_id = $1::bigint
          AND v.status = 'offen'
        RETURNING v.textversion, v.fahrertext_uebersetzt
        """;

    // Freigeben in EINEM Befehl. Nur wenn der Vorschlag noch offen ist und niemand ihn inzwischen geändert hat
    // (Textversion). $1 Firma, $2 Vorschlag, $3 Konto, $4 Empfänger, $5 Betreff, $6 Kundentext,
    // $7 Fahrertext deutsch, $8 Fahrertext übersetzt (leer = Übersetzung fehlt noch), $9 Sprache, $10 Textversion.
    private const string FreigebenQuery = """
        WITH v AS (
            SELECT v.vorschlag_id, v.fahrer_benutzer_id, v.kundentext, v.kundenempfaenger, v.fahrertext_original,
                   v.fahrertext_uebersetzt, v.fahrersprache, v.textversion, t.tour_id, t.unternehmen_id, a.auftragsnummer,
                   fb.vorname || ' ' || fb.nachname AS fahrer
            FROM public.winston_vorschlag v
            JOIN public.tourstopp s ON s.stopp_id = v.stopp_id
            JOIN public.tour t ON t.tour_id = s.tour_id
            JOIN public.auftrag a ON a.auftrag_id = v.auftrag_id
            JOIN public.benutzer fb ON fb.benutzer_id = v.fahrer_benutzer_id
            WHERE v.vorschlag_id = $2::bigint AND t.unternehmen_id = $1::bigint
              AND v.status = 'offen' AND v.textversion = $10::integer
        ), upd AS (
            UPDATE public.winston_vorschlag w
            SET status = 'freigegeben', kundenempfaenger = $4, kundentext = $6, fahrertext_original = $7,
                fahrertext_uebersetzt = NULLIF($8, ''), fahrersprache = $9,
                textversion = w.textversion + CASE WHEN v.kundentext IS DISTINCT FROM $6
                                                     OR v.kundenempfaenger IS DISTINCT FROM $4
                                                     OR v.fahrertext_original IS DISTINCT FROM $7
                                                     OR v.fahrertext_uebersetzt IS DISTINCT FROM NULLIF($8, '')
                                                     OR v.fahrersprache IS DISTINCT FROM $9 THEN 1 ELSE 0 END
            FROM v
            WHERE w.vorschlag_id = v.vorschlag_id
            RETURNING w.vorschlag_id, w.textversion
        ), fg AS (
            INSERT INTO public.freigabe (vorschlag_id, benutzer_id, entscheidung, gepruefte_textversion)
            SELECT vorschlag_id, $3::bigint, 'freigegeben', textversion FROM upd
            RETURNING freigabe_id, vorschlag_id
        ), vorhanden AS (
            SELECT c.chat_id FROM public.chat c, v
            WHERE c.tour_id = v.tour_id AND c.fahrer_benutzer_id = v.fahrer_benutzer_id
            ORDER BY c.chat_id LIMIT 1
        ), neuer_chat AS (
            INSERT INTO public.chat (status, tour_id, fahrer_benutzer_id, disponent_benutzer_id)
            SELECT 'offen', v.tour_id, v.fahrer_benutzer_id, $3::bigint FROM v, fg
            WHERE NOT EXISTS (SELECT 1 FROM vorhanden)
            RETURNING chat_id
        ), c AS (
            SELECT chat_id FROM vorhanden UNION ALL SELECT chat_id FROM neuer_chat
        ), n AS (
            INSERT INTO public.nachricht (nachrichtentext, originalsprache, zielsprache, uebersetzungstext,
                                          uebersetzungsstatus, uebertragungsstatus, chat_id, benutzer_id)
            SELECT $7, 'de', $9, CASE WHEN $9 = 'de' THEN NULL ELSE NULLIF($8, '') END,
                   CASE WHEN $9 = 'de' THEN 'nicht_noetig'
                        WHEN NULLIF($8, '') IS NULL THEN 'ausstehend' ELSE 'uebersetzt' END,
                   'gesendet', c.chat_id, $3::bigint
            FROM c, fg
            RETURNING nachricht_id, uebersetzungsstatus
        ), protokoll AS (
            INSERT INTO public.aktionsprotokoll (akteur_art, aktion, ergebnis, detail, kanal, empfaenger, versandtext,
                                                 unternehmen_id, benutzer_id, tour_id, vorschlag_id, freigabe_id)
            SELECT 'Benutzer', 'Kundeninformation freigegeben', 'Versand simuliert',
                   'Verspätungsinfo zu Sendung ' || v.auftragsnummer, 'E-Mail', $4, 'Betreff: ' || $5 || E'\n\n' || $6,
                   v.unternehmen_id, $3::bigint, v.tour_id, fg.vorschlag_id, fg.freigabe_id
            FROM v, fg
            UNION ALL
            SELECT 'Benutzer', 'Fahrerinformation freigegeben', 'Gesendet',
                   'Verspätungsinfo an den Fahrer (Chat)', 'Fahrer-App', v.fahrer, COALESCE(NULLIF($8, ''), $7),
                   v.unternehmen_id, $3::bigint, v.tour_id, fg.vorschlag_id, fg.freigabe_id
            FROM v, fg
        )
        SELECT n.nachricht_id, n.uebersetzungsstatus FROM n
        """;

    // Verwerfen mit Grund. $1 Firma, $2 Vorschlag, $3 Konto, $4 Grund, $5 Textversion.
    private const string VerwerfenQuery = """
        WITH v AS (
            SELECT v.vorschlag_id, v.textversion, t.tour_id, t.unternehmen_id, a.auftragsnummer
            FROM public.winston_vorschlag v
            JOIN public.tourstopp s ON s.stopp_id = v.stopp_id
            JOIN public.tour t ON t.tour_id = s.tour_id
            JOIN public.auftrag a ON a.auftrag_id = v.auftrag_id
            WHERE v.vorschlag_id = $2::bigint AND t.unternehmen_id = $1::bigint
              AND v.status = 'offen' AND v.textversion = $5::integer
        ), upd AS (
            UPDATE public.winston_vorschlag w SET status = 'verworfen' FROM v
            WHERE w.vorschlag_id = v.vorschlag_id RETURNING w.vorschlag_id
        ), fg AS (
            INSERT INTO public.freigabe (vorschlag_id, benutzer_id, entscheidung, gepruefte_textversion, grund)
            SELECT v.vorschlag_id, $3::bigint, 'verworfen', v.textversion, $4 FROM v, upd
            RETURNING freigabe_id, vorschlag_id
        ), protokoll AS (
            INSERT INTO public.aktionsprotokoll (akteur_art, aktion, ergebnis, detail, unternehmen_id, benutzer_id,
                                                 tour_id, vorschlag_id, freigabe_id)
            SELECT 'Benutzer', 'Verspätungsvorschlag verworfen', 'Gespeichert',
                   'Sendung ' || v.auftragsnummer || ' · Grund: ' || $4, v.unternehmen_id, $3::bigint, v.tour_id,
                   fg.vorschlag_id, fg.freigabe_id
            FROM v, fg
        )
        SELECT freigabe_id FROM fg
        """;

    /// <summary>
    /// Erzeugt neue Vorschläge (und setzt erledigte auf „veraltet“), stößt die Übersetzung der Fahrertexte an und
    /// lädt alle offenen Vorschläge.
    /// </summary>
    public async Task<IReadOnlyList<Verspaetungsvorschlag>> AktualisierenUndLadenAsync(CancellationToken cancellationToken = default)
    {
        FirmenZugriff zugriff = await AuthService.FirmenZugriffPruefenAsync(Rolle, cancellationToken);
        string firma = Text(zugriff.UnternehmenId);

        var neu = await _client.Value.QueryAsync(ErzeugenQuery, new string?[] { firma }, cancellationToken);
        // Übersetzung der neuen Fahrertexte. Schlägt sie fehl, bleibt der Text leer und „Neu übersetzen“ hilft.
        await Task.WhenAll(neu.Select(row => UebersetzungDienst.VorschlagUebersetzenAsync(Id(row, 0), cancellationToken)));

        var rows = await _client.Value.QueryAsync(OffeneQuery, new string?[] { firma }, cancellationToken);
        return rows.Select(r => new Verspaetungsvorschlag(
            Id(r, 0), Id(r, 1), Req(r, 2), Req(r, 3), Req(r, 4), Req(r, 5), Req(r, 6), Req(r, 7), Req(r, 8),
            Req(r, 9), Req(r, 10), Req(r, 11), r[12], Req(r, 13), Req(r, 14),
            int.Parse(Req(r, 15), CultureInfo.InvariantCulture), r[16], r[17],
            int.Parse(Req(r, 18), CultureInfo.InvariantCulture))).ToList();
    }

    /// <summary>Speichert die deutsche Fassung/Sprache für den Fahrer und lässt neu übersetzen.</summary>
    /// <returns>Neue Textversion und die Übersetzung (null = Übersetzung gerade nicht möglich).</returns>
    public async Task<(int Textversion, string? Uebersetzung)> FahrertextNeuUebersetzenAsync(long vorschlagId,
        string deutsch, string sprache, CancellationToken cancellationToken = default)
    {
        string text = Pflicht(deutsch, "Die deutsche Fassung für den Fahrer", 1000);
        FirmenZugriff zugriff = await AuthService.FirmenZugriffPruefenAsync(Rolle, cancellationToken);
        var rows = await _client.Value.QueryAsync(FahrertextQuery,
            new string?[] { Text(zugriff.UnternehmenId), Text(vorschlagId), text, sprache }, cancellationToken);
        if (rows.Count != 1)
            throw new ArgumentException("Der Vorschlag ist nicht mehr offen. Bitte „Aktualisieren“.");

        int version = int.Parse(Req(rows[0], 0), CultureInfo.InvariantCulture);
        if (rows[0][1] is string gleich)
            return (version, gleich); // gleiche Sprache, keine Übersetzung nötig

        await UebersetzungDienst.VorschlagUebersetzenAsync(vorschlagId, cancellationToken);
        var nach = await _client.Value.QueryAsync(
            "SELECT fahrertext_uebersetzt FROM public.winston_vorschlag WHERE vorschlag_id = $1::bigint",
            new string?[] { Text(vorschlagId) }, cancellationToken);
        return (version, nach.Count == 1 ? nach[0][0] : null);
    }

    /// <summary>
    /// Gibt frei: Kunden-E-Mail (simuliert, im Aktionsprotokoll) und Chat-Nachricht an den Fahrer.
    /// </summary>
    /// <exception cref="ArgumentException">Ungültige Eingaben oder Vorschlag inzwischen geändert/entschieden.</exception>
    public async Task FreigebenAsync(VorschlagFreigabe f, string tournummer, CancellationToken cancellationToken = default)
    {
        string empfaenger = Pflicht(f.Kundenempfaenger, "Empfänger", 254);
        if (!MailAddress.TryCreate(empfaenger, out var adresse) || adresse.Address != empfaenger)
            throw new ArgumentException("Bitte eine gültige E-Mail-Adresse als Empfänger eingeben.");
        string betreff = Pflicht(f.Betreff, "Betreff", 200);
        string kundentext = Pflicht(f.Kundentext, "Die Nachricht an den Kunden", 4000);
        // Wie im Wireframe: keine internen Tour- oder Stoppnummern an den Kunden.
        if (kundentext.Contains(tournummer, StringComparison.OrdinalIgnoreCase) ||
            betreff.Contains(tournummer, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"Bitte die interne Tournummer {tournummer} aus der Kundennachricht entfernen.");
        string deutsch = Pflicht(f.FahrertextDeutsch, "Die Nachricht an den Fahrer", 1000);
        string? ziel = string.IsNullOrWhiteSpace(f.FahrertextZiel) ? null : f.FahrertextZiel.Trim();
        if (ziel is { Length: > 1000 })
            throw new ArgumentException("Die Nachricht an den Fahrer darf höchstens 1000 Zeichen haben.");

        FirmenZugriff zugriff = await AuthService.FirmenZugriffPruefenAsync(Rolle, cancellationToken);
        var benutzer = BenutzerSitzung.AktuellerBenutzer
            ?? throw new UnauthorizedAccessException("Bitte melden Sie sich erneut an.");

        var rows = await _client.Value.QueryAsync(FreigebenQuery, new string?[]
        {
            Text(zugriff.UnternehmenId), Text(f.Id), Text(benutzer.Id), empfaenger, betreff, kundentext,
            deutsch, ziel ?? "", f.Fahrersprache, f.Textversion.ToString(CultureInfo.InvariantCulture)
        }, cancellationToken);
        if (rows.Count != 1)
            throw new ArgumentException("Der Vorschlag wurde inzwischen geändert oder schon entschieden. Bitte „Aktualisieren“.");

        // Fehlte die Übersetzung noch, wird sie jetzt für die Chat-Nachricht nachgeholt.
        if (Req(rows[0], 1) == UebersetzungsStatus.Ausstehend)
            await UebersetzungDienst.UebersetzenAsync(Id(rows[0], 0), cancellationToken);
    }

    /// <exception cref="ArgumentException">Kein Grund oder Vorschlag inzwischen geändert/entschieden.</exception>
    public async Task VerwerfenAsync(long vorschlagId, int textversion, string grund, CancellationToken cancellationToken = default)
    {
        string text = Pflicht(grund, "Der Grund", 500);
        FirmenZugriff zugriff = await AuthService.FirmenZugriffPruefenAsync(Rolle, cancellationToken);
        var benutzer = BenutzerSitzung.AktuellerBenutzer
            ?? throw new UnauthorizedAccessException("Bitte melden Sie sich erneut an.");
        var rows = await _client.Value.QueryAsync(VerwerfenQuery, new string?[]
        {
            Text(zugriff.UnternehmenId), Text(vorschlagId), Text(benutzer.Id), text,
            textversion.ToString(CultureInfo.InvariantCulture)
        }, cancellationToken);
        if (rows.Count != 1)
            throw new ArgumentException("Der Vorschlag wurde inzwischen geändert oder schon entschieden. Bitte „Aktualisieren“.");
    }

    private static string Pflicht(string? wert, string feld, int max)
    {
        string text = (wert ?? "").Trim();
        if (text.Length == 0)
            throw new ArgumentException($"{feld} fehlt.");
        if (text.Length > max)
            throw new ArgumentException($"{feld} darf höchstens {max} Zeichen haben.");
        return text;
    }

    private static long Id(string?[] row, int index) => long.Parse(Req(row, index), CultureInfo.InvariantCulture);

    private static string Text(long wert) => wert.ToString(CultureInfo.InvariantCulture);

    private static string Req(string?[] row, int index) => row[index]
        ?? throw new InvalidOperationException("In einem Pflichtfeld der Datenbank fehlt ein Wert.");
}
