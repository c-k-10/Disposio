using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Mail;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Disposio.Models;

namespace Disposio.Services;

/// <summary>
/// Sendungen (public.auftrag) für die Ansichten des Disponenten (D02, D03 …). Wie in den anderen Repositories wird zuerst
/// die Rolle Disponent geprüft und danach nur die Firma aus dieser Prüfung verwendet, nie eine Id aus der Oberfläche.
/// </summary>
public sealed class SendungRepository
{
    private const FirmenRolle Rolle = FirmenRolle.Disponent;

    // Erst beim ersten Zugriff verbinden, wie im UnternehmenRepository.
    private readonly Lazy<NeonSqlClient> _client = new(() => new NeonSqlClient());

    /// <summary>
    /// Alle Sendungen der Firma, die an diesem Tag unterwegs sind: Abholung am oder vor dem Tag,
    /// Zustellung am oder nach dem Tag. Unverplante zuerst, dann nach Lieferfenster.
    /// </summary>
    public async Task<IReadOnlyList<SendungUebersicht>> TagLadenAsync(DateOnly tag, CancellationToken cancellationToken = default)
    {
        FirmenZugriff zugriff = await AuthService.FirmenZugriffPruefenAsync(Rolle, cancellationToken);

        // Kunde und Tour nur, wenn sie zur selben Firma gehören.
        const string query = """
            SELECT a.auftrag_id, a.auftragsnummer, a.kundenreferenz, COALESCE(k.firmenname, ''),
                   a.abholadresse, a.lieferadresse,
                   to_char(a.abholdatum, 'YYYY-MM-DD'), to_char(a.lieferdatum, 'YYYY-MM-DD'),
                   to_char(a.lieferfenster_von, 'HH24:MI'), to_char(a.lieferfenster_bis, 'HH24:MI'),
                   a.gutart, a.anzahl, round(a.anzahl * a.laenge_m * a.breite_m / 2.4, 1),
                   a.status, a.prioritaet, t.tournummer
            FROM public.auftrag a
            LEFT JOIN public.kunde k ON k.kunde_id = a.kunde_id AND k.unternehmen_id = a.unternehmen_id
            LEFT JOIN public.tour t ON t.tour_id = a.tour_id AND t.unternehmen_id = a.unternehmen_id
            WHERE a.unternehmen_id = $1::bigint
              AND a.abholdatum <= $2::date AND a.lieferdatum >= $2::date
            ORDER BY (a.tour_id IS NOT NULL), a.lieferdatum, a.lieferfenster_von, a.auftragsnummer
            """;
        var rows = await _client.Value.QueryAsync(query, new string?[]
        {
            zugriff.UnternehmenId.ToString(CultureInfo.InvariantCulture),
            tag.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
        }, cancellationToken);

        var sendungen = new List<SendungUebersicht>();
        foreach (var row in rows)
        {
            sendungen.Add(new SendungUebersicht(
                long.Parse(Required(row, 0), CultureInfo.InvariantCulture),
                Required(row, 1),
                row[2],
                Required(row, 3),
                Required(row, 4),
                Required(row, 5),
                DateOnly.ParseExact(Required(row, 6), "yyyy-MM-dd", CultureInfo.InvariantCulture),
                DateOnly.ParseExact(Required(row, 7), "yyyy-MM-dd", CultureInfo.InvariantCulture),
                Required(row, 8),
                Required(row, 9),
                Required(row, 10),
                int.Parse(Required(row, 11), CultureInfo.InvariantCulture),
                // Neon liefert Zahlen mit Punkt als Dezimaltrenner. Deshalb InvariantCulture.
                decimal.Parse(Required(row, 12), CultureInfo.InvariantCulture),
                Required(row, 13),
                Required(row, 14),
                row[15]));
        }

        return sendungen;
    }

    // ── D03: Sendung erfassen / bearbeiten ───────────────────────────────────────────

    /// <summary>Kunden, Nummernvorschläge und beim Bearbeiten (id gesetzt) die vorhandene Sendung.</summary>
    /// <exception cref="ArgumentException">Die Sendung gibt es in dieser Firma nicht.</exception>
    public async Task<SendungFormularDaten> FormularLadenAsync(long? id, CancellationToken cancellationToken = default)
    {
        FirmenZugriff zugriff = await AuthService.FirmenZugriffPruefenAsync(Rolle, cancellationToken);
        var firma = new string?[] { Text(zugriff.UnternehmenId) };

        var kunden = (await _client.Value.QueryAsync(
                "SELECT kunde_id, kundennummer, firmenname FROM public.kunde WHERE unternehmen_id = $1::bigint ORDER BY lower(firmenname), kundennummer",
                firma, cancellationToken))
            .Select(row => new KundeAuswahl(Id(row, 0), Required(row, 1), Required(row, 2)))
            .ToArray();

        // Vorschläge: höchste Nummer der Form „A-1001“ bzw. „K-1001“ plus 1, sonst 1001.
        var nummern = await _client.Value.QueryAsync("""
                SELECT 'A-' || (COALESCE((SELECT max(substring(auftragsnummer FROM '^A-([0-9]{1,9})$')::bigint)
                                          FROM public.auftrag WHERE unternehmen_id = $1::bigint), 1000) + 1),
                       'K-' || (COALESCE((SELECT max(substring(kundennummer FROM '^K-([0-9]{1,9})$')::bigint)
                                          FROM public.kunde WHERE unternehmen_id = $1::bigint), 1000) + 1)
                """, firma, cancellationToken);

        SendungEntwurf? vorhanden = null;
        string? tournummer = null;
        if (id is long auftragId)
        {
            var rows = await _client.Value.QueryAsync("""
                SELECT a.auftragsnummer, a.kundenreferenz, a.kunde_id, a.abholadresse,
                       to_char(a.abholdatum, 'YYYY-MM-DD'), to_char(a.abholfenster_von, 'HH24:MI'), to_char(a.abholfenster_bis, 'HH24:MI'),
                       a.lieferadresse,
                       to_char(a.lieferdatum, 'YYYY-MM-DD'), to_char(a.lieferfenster_von, 'HH24:MI'), to_char(a.lieferfenster_bis, 'HH24:MI'),
                       a.ansprechpartner, a.avisierung_erforderlich, a.gutart, a.verpackungstyp, a.anzahl, a.gewicht_kg,
                       a.laenge_m, a.breite_m, a.hoehe_m, a.temperatur_min_c, a.temperatur_max_c, a.warenwert,
                       a.stapelbar, a.drehbar, a.gefahrgut, a.versicherung, a.ladehilfsmitteltausch, a.zollrelevanz,
                       a.lade_entladeequipment, a.prioritaet, a.status, t.tournummer
                FROM public.auftrag a
                LEFT JOIN public.tour t ON t.tour_id = a.tour_id AND t.unternehmen_id = a.unternehmen_id
                WHERE a.auftrag_id = $2::bigint AND a.unternehmen_id = $1::bigint
                """, new string?[] { Text(zugriff.UnternehmenId), Text(auftragId) }, cancellationToken);
            if (rows.Count != 1)
                throw new ArgumentException("Diese Sendung gibt es nicht (mehr).");

            var r = rows[0];
            vorhanden = new SendungEntwurf(
                Required(r, 0), r[1], Id(r, 2), null,
                Required(r, 3), Datum(r, 4), Uhrzeit(r, 5), Uhrzeit(r, 6),
                Required(r, 7), Datum(r, 8), Uhrzeit(r, 9), Uhrzeit(r, 10),
                Required(r, 11), Wahr(r, 12), Required(r, 13), Required(r, 14),
                int.Parse(Required(r, 15), CultureInfo.InvariantCulture),
                Zahl(r, 16), Zahl(r, 17), Zahl(r, 18), Zahl(r, 19),
                r[20] is string min ? decimal.Parse(min, CultureInfo.InvariantCulture) : null,
                r[21] is string max ? decimal.Parse(max, CultureInfo.InvariantCulture) : null,
                Zahl(r, 22),
                Wahr(r, 23), Wahr(r, 24), Wahr(r, 25), Wahr(r, 26), Wahr(r, 27), Wahr(r, 28),
                Required(r, 29), Required(r, 30), Required(r, 31));
            tournummer = r[32];
        }

        return new SendungFormularDaten(kunden, Required(nummern[0], 0), Required(nummern[0], 1), vorhanden, tournummer);
    }

    /// <summary>
    /// Legt eine Sendung an (id null) oder ändert eine unverplante Sendung. Ein neuer Kunde, die Sendung und der
    /// Protokolleintrag entstehen in einem einzigen Befehl: entweder alles oder nichts.
    /// </summary>
    /// <returns>Die Id der Sendung.</returns>
    /// <exception cref="ArgumentException">Ungültige Eingaben, Kunde gehört nicht zur Firma oder Sendung ist schon verplant.</exception>
    /// <exception cref="NummerVergebenException">Auftrags- oder Kundennummer gibt es in der Firma schon.</exception>
    public async Task<long> SpeichernAsync(SendungEntwurf entwurf, long? id, CancellationToken cancellationToken = default)
    {
        // Erst die Eingaben (ohne Datenbank), dann die Rechte (mit Datenbank) prüfen.
        SendungEntwurf e = Pruefen(entwurf);
        FirmenZugriff zugriff = await AuthService.FirmenZugriffPruefenAsync(Rolle, cancellationToken);

        // Alle Felder der Sendung als ein JSON-Objekt. Zahlen mit Punkt, Datum und Uhrzeit im ISO-Format.
        string auftragJson = JsonSerializer.Serialize(new
        {
            auftragsnummer = e.Auftragsnummer,
            kundenreferenz = e.Kundenreferenz,
            abholadresse = e.Abholadresse,
            abholdatum = e.Abholdatum.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            abholfenster_von = e.AbholfensterVon.ToString("HH:mm", CultureInfo.InvariantCulture),
            abholfenster_bis = e.AbholfensterBis.ToString("HH:mm", CultureInfo.InvariantCulture),
            lieferadresse = e.Lieferadresse,
            lieferdatum = e.Lieferdatum.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            lieferfenster_von = e.LieferfensterVon.ToString("HH:mm", CultureInfo.InvariantCulture),
            lieferfenster_bis = e.LieferfensterBis.ToString("HH:mm", CultureInfo.InvariantCulture),
            ansprechpartner = e.Ansprechpartner,
            avisierung_erforderlich = e.AvisierungErforderlich,
            gutart = e.Gutart,
            verpackungstyp = e.Verpackungstyp,
            anzahl = e.Anzahl,
            gewicht_kg = e.GewichtKg,
            laenge_m = e.LaengeM,
            breite_m = e.BreiteM,
            hoehe_m = e.HoeheM,
            temperatur_min_c = e.TemperaturMinC,
            temperatur_max_c = e.TemperaturMaxC,
            warenwert = e.Warenwert,
            stapelbar = e.Stapelbar,
            drehbar = e.Drehbar,
            gefahrgut = e.Gefahrgut,
            versicherung = e.Versicherung,
            ladehilfsmitteltausch = e.Ladehilfsmitteltausch,
            zollrelevanz = e.Zollrelevanz,
            lade_entladeequipment = e.LadeEntladeequipment,
            prioritaet = e.Prioritaet,
            status = e.Status
        });
        string? kundeJson = e.NeuerKunde is { } k
            ? JsonSerializer.Serialize(new
            {
                kundennummer = k.Kundennummer, firmenname = k.Firmenname, ansprechpartner = k.Ansprechpartner,
                adresse = k.Adresse, plz = k.Plz, ort = k.Ort, telefon = k.Telefon, email = k.Email
            })
            : null;

        // $1 Firma, $2 Konto, $3 Sendung (JSON), $4 neuer Kunde (JSON oder null), $5 bestehender Kunde, $6 Sendungs-Id (null = neu).
        // „frei“ liefert nur eine Zeile, wenn eine neue Sendung angelegt wird oder die bestehende zur Firma gehört
        // und noch keiner Tour zugeordnet ist. Ohne diese Zeile wird gar nichts gespeichert, auch kein neuer Kunde.
        const string query = """
            WITH e AS (
                SELECT * FROM jsonb_to_record($3::jsonb) AS x(
                    auftragsnummer text, kundenreferenz text, abholadresse text, abholdatum date,
                    abholfenster_von time, abholfenster_bis time, lieferadresse text, lieferdatum date,
                    lieferfenster_von time, lieferfenster_bis time, ansprechpartner text, avisierung_erforderlich boolean,
                    gutart text, verpackungstyp text, anzahl integer, gewicht_kg numeric, laenge_m numeric, breite_m numeric,
                    hoehe_m numeric, temperatur_min_c numeric, temperatur_max_c numeric, warenwert numeric,
                    stapelbar boolean, drehbar boolean, gefahrgut boolean, versicherung boolean,
                    ladehilfsmitteltausch boolean, zollrelevanz boolean, lade_entladeequipment text,
                    prioritaet text, status text)
            ), frei AS (
                SELECT 1 AS ok
                WHERE $6::bigint IS NULL
                   OR EXISTS (SELECT 1 FROM public.auftrag a
                              WHERE a.auftrag_id = $6::bigint AND a.unternehmen_id = $1::bigint AND a.tour_id IS NULL)
            ), neuer_kunde AS (
                INSERT INTO public.kunde (kundennummer, firmenname, ansprechpartner, adresse, plz, ort, telefon, email, unternehmen_id)
                SELECT k.kundennummer, k.firmenname, k.ansprechpartner, k.adresse, k.plz, k.ort, k.telefon, k.email, $1::bigint
                FROM frei, jsonb_to_record($4::jsonb) AS k(kundennummer text, firmenname text, ansprechpartner text,
                                                           adresse text, plz text, ort text, telefon text, email text)
                WHERE $4::jsonb IS NOT NULL
                RETURNING kunde_id
            ), kunde_wahl AS (
                SELECT kunde_id FROM neuer_kunde
                UNION ALL
                SELECT k.kunde_id FROM public.kunde k, frei
                WHERE $4::jsonb IS NULL AND k.kunde_id = $5::bigint AND k.unternehmen_id = $1::bigint
            ), angelegt AS (
                INSERT INTO public.auftrag (auftragsnummer, kunde_id, kundenreferenz, abholadresse, lieferadresse,
                    abholdatum, lieferdatum, abholfenster_von, abholfenster_bis, lieferfenster_von, lieferfenster_bis,
                    gewicht_kg, laenge_m, breite_m, hoehe_m, verpackungstyp, anzahl, gutart, temperatur_min_c, temperatur_max_c,
                    stapelbar, drehbar, gefahrgut, warenwert, versicherung, ansprechpartner, avisierung_erforderlich,
                    ladehilfsmitteltausch, lade_entladeequipment, zollrelevanz, status, prioritaet, unternehmen_id)
                SELECT e.auftragsnummer, kw.kunde_id, e.kundenreferenz, e.abholadresse, e.lieferadresse,
                    e.abholdatum, e.lieferdatum, e.abholfenster_von, e.abholfenster_bis, e.lieferfenster_von, e.lieferfenster_bis,
                    e.gewicht_kg, e.laenge_m, e.breite_m, e.hoehe_m, e.verpackungstyp, e.anzahl, e.gutart, e.temperatur_min_c, e.temperatur_max_c,
                    e.stapelbar, e.drehbar, e.gefahrgut, e.warenwert, e.versicherung, e.ansprechpartner, e.avisierung_erforderlich,
                    e.ladehilfsmitteltausch, e.lade_entladeequipment, e.zollrelevanz, e.status, e.prioritaet, $1::bigint
                FROM e, kunde_wahl kw
                WHERE $6::bigint IS NULL
                RETURNING auftrag_id, auftragsnummer
            ), geaendert AS (
                UPDATE public.auftrag a SET
                    auftragsnummer = e.auftragsnummer, kunde_id = kw.kunde_id, kundenreferenz = e.kundenreferenz,
                    abholadresse = e.abholadresse, lieferadresse = e.lieferadresse,
                    abholdatum = e.abholdatum, lieferdatum = e.lieferdatum,
                    abholfenster_von = e.abholfenster_von, abholfenster_bis = e.abholfenster_bis,
                    lieferfenster_von = e.lieferfenster_von, lieferfenster_bis = e.lieferfenster_bis,
                    gewicht_kg = e.gewicht_kg, laenge_m = e.laenge_m, breite_m = e.breite_m, hoehe_m = e.hoehe_m,
                    verpackungstyp = e.verpackungstyp, anzahl = e.anzahl, gutart = e.gutart,
                    temperatur_min_c = e.temperatur_min_c, temperatur_max_c = e.temperatur_max_c,
                    stapelbar = e.stapelbar, drehbar = e.drehbar, gefahrgut = e.gefahrgut, warenwert = e.warenwert,
                    versicherung = e.versicherung, ansprechpartner = e.ansprechpartner,
                    avisierung_erforderlich = e.avisierung_erforderlich, ladehilfsmitteltausch = e.ladehilfsmitteltausch,
                    lade_entladeequipment = e.lade_entladeequipment, zollrelevanz = e.zollrelevanz,
                    prioritaet = e.prioritaet
                FROM e, kunde_wahl kw
                WHERE a.auftrag_id = $6::bigint AND a.unternehmen_id = $1::bigint AND a.tour_id IS NULL
                RETURNING a.auftrag_id, a.auftragsnummer
            ), ergebnis AS (
                SELECT auftrag_id, auftragsnummer, 'Sendung angelegt' AS aktion FROM angelegt
                UNION ALL
                SELECT auftrag_id, auftragsnummer, 'Sendung bearbeitet' FROM geaendert
            ), protokoll AS (
                INSERT INTO public.aktionsprotokoll (akteur_art, aktion, ergebnis, detail, unternehmen_id, abteilung_id, benutzer_id)
                SELECT CASE WHEN $2::bigint IS NULL THEN 'Entwicklungsansicht' ELSE 'Benutzer' END,
                       r.aktion, 'Gespeichert', 'Sendung ' || r.auftragsnummer, $1::bigint, NULL, $2::bigint
                FROM ergebnis r
            )
            SELECT auftrag_id FROM ergebnis
            """;
        var parameters = new string?[]
        {
            Text(zugriff.UnternehmenId),
            // Wer speichert: das angemeldete Konto (beim Admin-Zugang der MegakrasserAdmin selbst).
            BenutzerSitzung.AktuellerBenutzer is { } benutzer ? Text(benutzer.Id) : null,
            auftragJson,
            kundeJson,
            e.KundeId is long kundeId ? Text(kundeId) : null,
            id is long auftragId ? Text(auftragId) : null
        };

        try
        {
            var rows = await _client.Value.QueryAsync(query, parameters, cancellationToken);
            if (rows.Count == 1)
                return Id(rows[0], 0);

            throw new ArgumentException(id is null
                ? "Die Sendung konnte nicht gespeichert werden: Den gewählten Kunden gibt es nicht mehr. Bitte laden Sie das Formular neu."
                : "Die Sendung konnte nicht gespeichert werden: Sie wurde inzwischen einer Tour zugeordnet oder gelöscht.");
        }
        catch (NeonSqlException exception) when (exception.Code == "23505")
        {
            // Die Datenbank prüft eindeutige Nummern je Firma, auch bei zwei gleichzeitigen Speichervorgängen.
            bool kunde = exception.ConstraintName?.Contains("kunde", StringComparison.OrdinalIgnoreCase) == true;
            throw new NummerVergebenException(kunde
                ? "Diese Kundennummer gibt es in Ihrem Unternehmen schon. Bitte eine andere wählen."
                : "Diese Auftragsnummer gibt es in Ihrem Unternehmen schon. Bitte eine andere wählen.", exception);
        }
    }

    /// <summary>Prüft die Eingaben noch einmal unabhängig von der Oberfläche. Die Regeln entsprechen den CHECKs in Neon.</summary>
    private static SendungEntwurf Pruefen(SendungEntwurf e)
    {
        ArgumentNullException.ThrowIfNull(e);
        if ((e.KundeId is null) == (e.NeuerKunde is null))
            throw new ArgumentException("Bitte einen Kunden wählen oder einen neuen Kunden anlegen.");
        if (e.Lieferdatum < e.Abholdatum)
            throw new ArgumentException("Das Lieferdatum darf nicht vor dem Abholdatum liegen.");
        if (e.AbholfensterBis < e.AbholfensterVon || e.LieferfensterBis < e.LieferfensterVon)
            throw new ArgumentException("Bei einem Zeitfenster liegt „bis“ vor „von“.");
        if (e.Anzahl is < 1 or > 10_000)
            throw new ArgumentException("Die Anzahl muss zwischen 1 und 10.000 liegen.");
        Bereich(e.GewichtKg, 0.01m, 60_000, "Das Gewicht (kg)");
        Bereich(e.LaengeM, 0.01m, 20, "Die Länge (m)");
        Bereich(e.BreiteM, 0.01m, 3, "Die Breite (m)");
        Bereich(e.HoeheM, 0.01m, 4, "Die Höhe (m)");
        Bereich(e.Warenwert, 0, 100_000_000, "Der Warenwert (€)");
        if (!SendungWerte.Gutarten.Contains(e.Gutart))
            throw new ArgumentException("Bitte eine Gutart wählen.");
        if (!SendungWerte.Verpackungstypen.Contains(e.Verpackungstyp))
            throw new ArgumentException("Bitte einen Verpackungstyp wählen.");
        if (!SendungWerte.Prioritaeten.Contains(e.Prioritaet))
            throw new ArgumentException("Bitte eine Priorität wählen.");
        if (!SendungWerte.Equipment.Contains(e.LadeEntladeequipment))
            throw new ArgumentException("Bitte das Lade-/Entladeequipment wählen.");

        bool gekuehlt = e.Gutart == SendungWerte.GutartFrische;
        if (gekuehlt && (e.TemperaturMinC is null || e.TemperaturMaxC is null))
            throw new ArgumentException("Bei Frische sind Mindest- und Höchsttemperatur Pflicht.");
        if (e.TemperaturMinC is decimal min && e.TemperaturMaxC is decimal max && min > max)
            throw new ArgumentException("Die Mindesttemperatur darf nicht über der Höchsttemperatur liegen.");
        foreach (decimal? t in new[] { e.TemperaturMinC, e.TemperaturMaxC })
            if (t is < -30 or > 30)
                throw new ArgumentException("Temperaturen müssen zwischen -30 und 30 °C liegen.");

        NeuerKunde? kunde = e.NeuerKunde is { } k
            ? new NeuerKunde(
                Pflichttext(k.Kundennummer, "Kundennummer", 30), Pflichttext(k.Firmenname, "Firmenname", 200),
                Pflichttext(k.Ansprechpartner, "Ansprechpartner des Kunden", 200), Pflichttext(k.Adresse, "Adresse des Kunden", 300),
                Pflichttext(k.Plz, "PLZ", 10), Pflichttext(k.Ort, "Ort", 100),
                Pflichttext(k.Telefon, "Telefon", 50), Email(k.Email))
            : null;

        string? referenz = string.IsNullOrWhiteSpace(e.Kundenreferenz) ? null : e.Kundenreferenz.Trim();
        if (referenz is { Length: > 100 })
            throw new ArgumentException("Die Kundenreferenz darf höchstens 100 Zeichen enthalten.");

        return e with
        {
            Auftragsnummer = Pflichttext(e.Auftragsnummer, "Auftragsnummer", 30),
            Kundenreferenz = referenz,
            NeuerKunde = kunde,
            Abholadresse = Pflichttext(e.Abholadresse, "Abholadresse", 300),
            Lieferadresse = Pflichttext(e.Lieferadresse, "Lieferadresse", 300),
            Ansprechpartner = Pflichttext(e.Ansprechpartner, "Ansprechpartner vor Ort", 200),
            // Ohne Kühlung gibt es keine Temperaturvorgabe.
            TemperaturMinC = gekuehlt ? e.TemperaturMinC : null,
            TemperaturMaxC = gekuehlt ? e.TemperaturMaxC : null
        };
    }

    private static void Bereich(decimal wert, decimal minimum, decimal maximum, string feld)
    {
        if (wert < minimum || wert > maximum)
            throw new ArgumentException($"{feld} muss zwischen {minimum.ToString(CultureInfo.GetCultureInfo("de-DE"))} und " +
                                        $"{maximum.ToString("N0", CultureInfo.GetCultureInfo("de-DE"))} liegen.");
    }

    private static string Pflichttext(string? text, string feld, int maximaleLaenge)
    {
        string wert = (text ?? string.Empty).Trim();
        if (wert.Length == 0 || wert.Length > maximaleLaenge)
            throw new ArgumentException($"{feld} muss ausgefüllt sein und darf höchstens {maximaleLaenge} Zeichen enthalten.");
        return wert;
    }

    private static string Email(string? text)
    {
        string wert = Pflichttext(text, "E-Mail des Kunden", 254);
        if (!MailAddress.TryCreate(wert, out MailAddress? adresse) || adresse.Address != wert)
            throw new ArgumentException("Bitte eine gültige E-Mail-Adresse für den Kunden eingeben.");
        return wert;
    }

    private static DateOnly Datum(string?[] row, int index) =>
        DateOnly.ParseExact(Required(row, index), "yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static TimeOnly Uhrzeit(string?[] row, int index) =>
        TimeOnly.ParseExact(Required(row, index), "HH:mm", CultureInfo.InvariantCulture);

    private static decimal Zahl(string?[] row, int index) => decimal.Parse(Required(row, index), CultureInfo.InvariantCulture);

    // Neon liefert Wahrheitswerte als Text: t oder f.
    private static bool Wahr(string?[] row, int index) => Required(row, index) == "t";

    private static long Id(string?[] row, int index) => long.Parse(Required(row, index), CultureInfo.InvariantCulture);

    private static string Text(long wert) => wert.ToString(CultureInfo.InvariantCulture);

    private static string Required(string?[] row, int index) => row[index]
        ?? throw new InvalidOperationException("In einem Pflichtfeld der Datenbank fehlt ein Wert.");
}

/// <summary>Eine verständliche Meldung statt des technischen PostgreSQL-Fehlers.</summary>
public sealed class NummerVergebenException : Exception
{
    public NummerVergebenException(string meldung, Exception innerException) : base(meldung, innerException) { }
}
