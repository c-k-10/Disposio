using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Disposio.Models;

namespace Disposio.Services;

/// <summary>
/// Prüfungen für den „Systemzustand“ im Admin-Dashboard (nur MegakrasserAdmin). Liest nur, ändert nichts.
/// Jede Prüfung liefert ein <see cref="Pruefergebnis"/> mit Ampel. Neue Prüfung: eine Methode schreiben und in
/// <see cref="AllePruefenAsync"/> ergänzen. Der Admin sieht nur Zahlen und Firmennamen, keine Nachrichtentexte.
/// </summary>
public sealed class SystemzustandRepository
{
    // Gleiche Adresse wie in Disposio.web/js/config.js (sendungApiUrl).
    private const string SendungFunktionsUrl = "https://br-shy-darkness-b21twd96-sendung.compute.c-6.eu-central-1.aws.neon.tech/";

    // Spalten, die eine Migration anlegt. Fehlt eine, wurde die Datei im Neon SQL Editor nicht ausgeführt.
    // Neue Migration mit neuer Spalte: hier eine Zeile ergänzen.
    private static readonly (string Tabelle, string Spalte, string Datei)[] ErwarteteSpalten =
    {
        ("unternehmen", "kennung", "001_unternehmen_dashboard.sql"),
        ("unternehmen", "admin_email", "001_unternehmen_dashboard.sql"),
        ("fahrzeug", "aktiv", "004_fahrzeug_aktiv.sql"),
        ("abteilung", "aktiv", "006_abteilung_aktiv.sql"),
        ("unternehmen", "anmelde_code", "009_web_anmeldung.sql"),
        ("wartung", "kilometerstand_bei_durchfuehrung", "010_fahrerzuordnung_wartung.sql"),
        ("benutzer", "letzte_abmeldung", "011_letzte_abmeldung.sql"),
    };

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    private readonly Lazy<NeonSqlClient> _client = new(() => new NeonSqlClient());

    /// <summary>Alle Prüfungen, in fester Reihenfolge (so bleiben die Kacheln an ihrem Platz).</summary>
    /// <exception cref="UnauthorizedAccessException">Kein MegakrasserAdmin (mehr).</exception>
    public async Task<IReadOnlyList<Pruefergebnis>> AllePruefenAsync(CancellationToken cancellationToken = default)
    {
        // Ohne erreichbare Datenbank kann auch die Rolle nicht geprüft werden. Dann trotzdem anzeigen,
        // dass die Datenbank fehlt – das ist genau der Fall, in dem der Admin den Systemzustand braucht.
        Pruefergebnis datenbank = await DatenbankPruefenAsync(cancellationToken);
        bool dbErreichbar = datenbank.Stufe != Pruefstufe.Kritisch;
        if (dbErreichbar)
            await AuthService.DashboardZugriffPruefenAsync(cancellationToken);

        Task<Pruefergebnis> funktionen = FunktionenPruefenAsync(cancellationToken);
        Task<Pruefergebnis> uebersetzung = Sicher("Übersetzung", dbErreichbar, UebersetzungPruefenAsync, cancellationToken);
        Task<Pruefergebnis> migrationen = Sicher("Migrationen", dbErreichbar, MigrationenPruefenAsync, cancellationToken);
        Task<Pruefergebnis> kundenadmins = Sicher("Kundenadmins", dbErreichbar, KundenadminsPruefenAsync, cancellationToken);
        Task<Pruefergebnis> webseite = Sicher("Web-Anmeldungen", dbErreichbar, WebAnmeldungenPruefenAsync, cancellationToken);
        await Task.WhenAll(funktionen, uebersetzung, migrationen, kundenadmins, webseite);

        return new[] { datenbank, await funktionen, await uebersetzung, await migrationen, await kundenadmins, await webseite };
    }

    // ── Datenbank ─────────────────────────────────────────────────────────────────────

    private async Task<Pruefergebnis> DatenbankPruefenAsync(CancellationToken cancellationToken)
    {
        var uhr = Stopwatch.StartNew();
        try
        {
            using var zeitlimit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            zeitlimit.CancelAfter(TimeSpan.FromSeconds(8));
            await _client.Value.QueryAsync("SELECT 1", Array.Empty<string?>(), zeitlimit.Token);
            long ms = uhr.ElapsedMilliseconds;
            return ms > 2000
                ? new Pruefergebnis("Datenbank", Pruefstufe.Warnung, $"Langsam: {ms} ms",
                    new[] { $"Eine einfache Abfrage dauerte {ms} ms (normal: unter 500 ms)." },
                    "Meist ein Kaltstart von Neon nach längerer Pause. Bleibt es langsam, den Neon-Status prüfen.")
                : new Pruefergebnis("Datenbank", Pruefstufe.Ok, $"Antwort in {ms} ms", Array.Empty<string>(), "");
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return new Pruefergebnis("Datenbank", Pruefstufe.Kritisch, "Nicht erreichbar",
                new[] { "Neon hat auf eine einfache Abfrage nicht geantwortet." },
                "Internetverbindung und Neon-Konsole prüfen (Projekt aktiv, Zugangsdaten gültig). Ohne Datenbank funktioniert keine Ansicht.");
        }
    }

    // ── Neon Functions ────────────────────────────────────────────────────────────────

    private static async Task<Pruefergebnis> FunktionenPruefenAsync(CancellationToken cancellationToken)
    {
        var ziele = new (string Name, string Url, string Wofuer)[]
        {
            ("konto", BenutzerKontoDienst.FunktionsUrl, "Konten anlegen (Kundenadmin)"),
            ("sendung", SendungFunktionsUrl, "Sendungen über die Webseite"),
            ("uebersetzen", UebersetzungDienst.FunktionsUrl, "Übersetzung im Chat"),
        };
        var ergebnisse = await Task.WhenAll(ziele.Select(async ziel =>
            (ziel.Name, ziel.Wofuer, Fehler: await FunktionTestenAsync(ziel.Url, cancellationToken))));

        var kaputt = ergebnisse.Where(e => e.Fehler is not null).ToList();
        if (kaputt.Count == 0)
            return new Pruefergebnis("Neon Functions", Pruefstufe.Ok, $"Alle {ziele.Length} erreichbar",
                ergebnisse.Select(e => $"{e.Name}: erreichbar").ToArray(), "");

        return new Pruefergebnis("Neon Functions", Pruefstufe.Kritisch,
            kaputt.Count == 1 ? $"„{kaputt[0].Name}“ nicht erreichbar" : $"{kaputt.Count} nicht erreichbar",
            ergebnisse.Select(e => e.Fehler is null ? $"{e.Name}: erreichbar" : $"{e.Name}: {e.Fehler} – betrifft {e.Wofuer}").ToArray(),
            "Im Ordner neon-function veröffentlichen: neon functions deploy <name> --src functions/<name>.ts " +
            "(bei „uebersetzen“ mit --env DEEPL_API_KEY=…). Danach mit „neon functions get <name>“ die Adresse vergleichen.");
    }

    // null = läuft. Ein GET reicht: Die Funktionen antworten darauf mit 405 („Nur POST“) bzw. 200.
    private static async Task<string?> FunktionTestenAsync(string url, CancellationToken cancellationToken)
    {
        try
        {
            using var antwort = await Http.GetAsync(url, cancellationToken).ConfigureAwait(false);
            if (antwort.StatusCode == HttpStatusCode.NotFound)
                return "nicht veröffentlicht (404)";
            if ((int)antwort.StatusCode >= 500)
                return $"Serverfehler ({(int)antwort.StatusCode})";
            return null;
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return "keine Antwort";
        }
    }

    // ── Übersetzung ───────────────────────────────────────────────────────────────────

    private async Task<Pruefergebnis> UebersetzungPruefenAsync(CancellationToken cancellationToken)
    {
        // Je Firma: fehlgeschlagene und hängende (seit > 10 Min. „ausstehend“) Übersetzungen der letzten 24 Stunden.
        const string query = """
            SELECT u.firmenname,
                   count(*) FILTER (WHERE n.uebersetzungsstatus = 'fehlgeschlagen'),
                   count(*) FILTER (WHERE n.uebersetzungsstatus = 'ausstehend' AND n.zeitstempel < now() - interval '10 minutes'),
                   count(*)
            FROM public.nachricht n
            JOIN public.chat c ON c.chat_id = n.chat_id
            JOIN public.tour t ON t.tour_id = c.tour_id
            JOIN public.unternehmen u ON u.unternehmen_id = t.unternehmen_id
            WHERE n.zeitstempel > now() - interval '24 hours'
            GROUP BY u.firmenname
            ORDER BY 2 DESC, 3 DESC, u.firmenname
            """;
        var rows = await _client.Value.QueryAsync(query, Array.Empty<string?>(), cancellationToken);

        int fehler = rows.Sum(r => Zahl(r, 1) + Zahl(r, 2));
        int gesamt = rows.Sum(r => Zahl(r, 3));
        if (fehler == 0)
            return new Pruefergebnis("Übersetzung", Pruefstufe.Ok,
                gesamt == 0 ? "Keine Nachrichten in 24 h" : $"{gesamt} Nachrichten in 24 h, keine Fehler",
                Array.Empty<string>(), "");

        var details = rows.Where(r => Zahl(r, 1) + Zahl(r, 2) > 0)
            .Select(r => $"{r[0]}: {Zahl(r, 1)} fehlgeschlagen, {Zahl(r, 2)} hängen" +
                         $" (von {Zahl(r, 3)} Nachrichten)")
            .ToArray();
        // Kritisch: viele Fehler oder gar keine erfolgreiche Übersetzung mehr.
        Pruefstufe stufe = fehler >= 5 || fehler == gesamt ? Pruefstufe.Kritisch : Pruefstufe.Warnung;
        return new Pruefergebnis("Übersetzung", stufe, $"{fehler} von {gesamt} Nachrichten in 24 h ohne Übersetzung",
            details,
            "Im DeepL-Konto unter „Usage“ das Kontingent prüfen und ob die Funktion „uebersetzen“ erreichbar ist. " +
            "Die Originale sind gespeichert; im Chat „Erneut übersetzen“ klicken.");
    }

    // ── Migrationen ───────────────────────────────────────────────────────────────────

    private async Task<Pruefergebnis> MigrationenPruefenAsync(CancellationToken cancellationToken)
    {
        const string query = """
            SELECT column_name, table_name FROM information_schema.columns
            WHERE table_schema = 'public'
            """;
        var rows = await _client.Value.QueryAsync(query, Array.Empty<string?>(), cancellationToken);
        var vorhanden = rows.Select(r => $"{r[1]}.{r[0]}").ToHashSet(StringComparer.OrdinalIgnoreCase);

        var fehlend = ErwarteteSpalten.Where(s => !vorhanden.Contains($"{s.Tabelle}.{s.Spalte}")).ToList();
        if (fehlend.Count == 0)
            return new Pruefergebnis("Migrationen", Pruefstufe.Ok,
                $"Alle {ErwarteteSpalten.Select(s => s.Datei).Distinct().Count()} geprüften ausgeführt", Array.Empty<string>(), "");

        var dateien = fehlend.Select(s => s.Datei).Distinct().ToList();
        return new Pruefergebnis("Migrationen", Pruefstufe.Kritisch,
            dateien.Count == 1 ? $"{dateien[0]} fehlt" : $"{dateien.Count} Migrationen fehlen",
            fehlend.Select(s => $"Spalte {s.Tabelle}.{s.Spalte} fehlt → {s.Datei}").ToArray(),
            "Die genannten Dateien aus src/Disposio/Disposio/Services im Neon SQL Editor ausführen (in der Reihenfolge der Nummern). " +
            "Bis dahin schlagen die zugehörigen Ansichten fehl.");
    }

    // ── Firmen ohne Kundenadmin ───────────────────────────────────────────────────────

    private async Task<Pruefergebnis> KundenadminsPruefenAsync(CancellationToken cancellationToken)
    {
        // Wie in neon-function/functions/konto.ts: die Standardrolle „Kundenadmin“ (rolle.unternehmen_id IS NULL).
        const string query = """
            SELECT u.firmenname FROM public.unternehmen u
            WHERE u.status <> 'Inaktiv'
              AND NOT EXISTS (
                  SELECT 1 FROM public.benutzer b
                  JOIN public.benutzer_rolle br ON br.benutzer_id = b.benutzer_id
                  JOIN public.rolle r ON r.rollen_id = br.rollen_id
                  WHERE b.unternehmen_id = u.unternehmen_id AND b.aktiv
                    AND r.bezeichnung = 'Kundenadmin' AND r.unternehmen_id IS NULL)
            ORDER BY lower(u.firmenname)
            """;
        var rows = await _client.Value.QueryAsync(query, Array.Empty<string?>(), cancellationToken);
        if (rows.Count == 0)
            return new Pruefergebnis("Kundenadmins", Pruefstufe.Ok, "Jede aktive Firma hat einen", Array.Empty<string>(), "");

        return new Pruefergebnis("Kundenadmins", Pruefstufe.Warnung,
            rows.Count == 1 ? $"{rows[0][0]} hat keinen" : $"{rows.Count} Firmen ohne Kundenadmin",
            rows.Select(r => $"{r[0]}: kein aktiver Kundenadmin").ToArray(),
            "Die Firma kann keine Konten mehr verwalten. Unter „Kunden“ die Firma bearbeiten und einen Kundenadmin anlegen oder reaktivieren.");
    }

    // ── Sendungen über die Webseite ───────────────────────────────────────────────────

    private async Task<Pruefergebnis> WebAnmeldungenPruefenAsync(CancellationToken cancellationToken)
    {
        // Die Funktion „sendung“ nimmt je Firma höchstens 30 Web-Sendungen pro Stunde an (MAX_PRO_STUNDE).
        const string query = """
            SELECT u.firmenname, count(*)
            FROM public.aktionsprotokoll p
            JOIN public.unternehmen u ON u.unternehmen_id = p.unternehmen_id
            WHERE p.akteur_art = 'Webseite' AND p.zeitpunkt > now() - interval '1 hour'
            GROUP BY u.firmenname
            ORDER BY 2 DESC
            """;
        var rows = await _client.Value.QueryAsync(query, Array.Empty<string?>(), cancellationToken);
        int gesamt = rows.Sum(r => Zahl(r, 1));
        var auffaellig = rows.Where(r => Zahl(r, 1) >= 20).ToList();
        if (auffaellig.Count == 0)
            return new Pruefergebnis("Web-Anmeldungen", Pruefstufe.Ok,
                gesamt == 1 ? "1 Sendung in der letzten Stunde" : $"{gesamt} Sendungen in der letzten Stunde",
                Array.Empty<string>(), "");

        bool grenze = auffaellig.Any(r => Zahl(r, 1) >= 30);
        return new Pruefergebnis("Web-Anmeldungen", grenze ? Pruefstufe.Kritisch : Pruefstufe.Warnung,
            grenze ? "Stundengrenze erreicht" : "Ungewöhnlich viele Anfragen",
            auffaellig.Select(r => $"{r[0]}: {Zahl(r, 1)} von höchstens 30 pro Stunde").ToArray(),
            grenze
                ? "Weitere Kunden dieser Firma werden gerade abgewiesen. Möglicher Spam: Anfragen mit Status „Angefragt“ prüfen; " +
                  "falls nötig in der Datenbank den anmelde_code der Firma neu würfeln."
                : "Im Auge behalten. Ab 30 pro Stunde weist die Webseite weitere Anfragen dieser Firma ab.");
    }

    // ── Hilfen ────────────────────────────────────────────────────────────────────────

    // Eine Prüfung, die selbst scheitert (z. B. Tabelle fehlt), darf die anderen nicht mitreißen.
    private static async Task<Pruefergebnis> Sicher(string name, bool dbErreichbar,
        Func<CancellationToken, Task<Pruefergebnis>> pruefung, CancellationToken cancellationToken)
    {
        if (!dbErreichbar)
            return new Pruefergebnis(name, Pruefstufe.NichtGeprueft, "Nicht geprüft",
                new[] { "Die Datenbank ist nicht erreichbar." }, "");
        try
        {
            return await pruefung(cancellationToken);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return new Pruefergebnis(name, Pruefstufe.Warnung, "Prüfung fehlgeschlagen",
                new[] { "Die Abfrage für diese Prüfung ist mit einem Fehler abgebrochen." },
                "Oft fehlt eine Migration (siehe Kachel „Migrationen“). Sonst „Jetzt prüfen“ erneut versuchen.");
        }
    }

    private static int Zahl(string?[] row, int index) =>
        int.Parse(row[index] ?? "0", CultureInfo.InvariantCulture);
}
