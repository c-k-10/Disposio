using System.Globalization;
using Disposio.Api.Endpoints;
using Npgsql;

namespace Disposio.Api.Services;

/// <summary>
/// v0.7: Die Software schreibt direkt in Neon. Darum prueft die API alle 20 Sekunden selbst,
/// ob es neue Nachrichten vom Disponenten oder neu zugewiesene Touren gibt, und sendet dann einen Push
/// an das Thema des Fahrers (siehe PushDienst). Ohne Firebase-Zugang wird nichts abgefragt.
///
/// Nachrichten: fremde (Absender ist nicht der Fahrer des Chats) und ungelesen.
/// Beim Start gilt "letzte Pruefung" = jetzt - 10 Minuten (nur noch nicht abgeholte), danach alles mit hoeherer nachricht_id
/// (plus 1 Minute Ueberlappung fuer spaet bestaetigte Transaktionen). Gesendete IDs werden gemerkt.
///
/// Touren: Paare (tour_id, fahrer_id) von nicht abgeschlossenen Touren. Neues Paar = Push.
/// Der erste Lauf nach dem Start merkt sich die Paare nur.
/// </summary>
public sealed class BenachrichtigungsWaechter : BackgroundService
{
    private static readonly TimeSpan Takt = TimeSpan.FromSeconds(20);
    private const int MaxVorschau = 80;
    private const int MaxGemerkt = 5000;

    private readonly NpgsqlDataSource _db;
    private readonly PushDienst _push;
    private readonly ILogger<BenachrichtigungsWaechter> _log;

    private long _hoechsteNachrichtId;
    private bool _ersterNachrichtenLauf = true;
    private readonly HashSet<long> _gesendeteNachrichten = new();
    private readonly Queue<long> _gesendeteReihenfolge = new();

    private HashSet<(long TourId, long FahrerId)>? _bekannteTouren;

    public BenachrichtigungsWaechter(NpgsqlDataSource db, PushDienst push, ILogger<BenachrichtigungsWaechter> log)
    {
        _db = db;
        _push = push;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken abbruch)
    {
        if (!_push.Aktiv)
        {
            return;
        }

        using var takt = new PeriodicTimer(Takt);
        do
        {
            try
            {
                await NachrichtenAsync(abbruch);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogWarning(ex, "Pruefung auf neue Nachrichten fehlgeschlagen");
            }

            try
            {
                await TourenAsync(abbruch);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogWarning(ex, "Pruefung auf neue Touren fehlgeschlagen");
            }
        }
        while (await takt.WaitForNextTickAsync(abbruch));
    }

    private async Task NachrichtenAsync(CancellationToken abbruch)
    {
        if (_ersterNachrichtenLauf)
        {
            // Startpunkt merken: spaetere Laeufe nehmen nur hoehere IDs, sonst kaemen alle alten ungelesenen
            await using var max = _db.CreateCommand("SELECT COALESCE(max(nachricht_id), 0) FROM public.nachricht");
            _hoechsteNachrichtId = Math.Max(_hoechsteNachrichtId, (long)(await max.ExecuteScalarAsync(abbruch))!);
        }

        // Erster Lauf: nur Nachrichten, die die App noch nicht abgeholt hat (sonst nach einem Neustart doppelt).
        // Danach jede neue Nachricht, auch wenn die offene App sie schon abgeholt hat (uebertragungsstatus
        // zugestellt): dann zeigt die App selbst eine lokale Benachrichtigung, ausser der Chat ist offen.
        var bedingung = _ersterNachrichtenLauf
            ? $"n.uebertragungsstatus = '{ChatEndpoints.Gesendet}' AND n.zeitstempel > now() - interval '10 minutes'"
            : "(n.nachricht_id > $1 OR n.zeitstempel > now() - interval '1 minute')";

        await using var befehl = _db.CreateCommand($"""
            SELECT n.nachricht_id, n.chat_id, n.nachrichtentext, n.uebersetzungstext, n.uebersetzungsstatus,
                   n.zielsprache, f.benutzer_id, f.sprache, d.vorname, d.nachname
            FROM public.nachricht n
            JOIN public.chat c ON c.chat_id = n.chat_id
            JOIN public.benutzer f ON f.benutzer_id = c.fahrer_benutzer_id
            JOIN public.benutzer d ON d.benutzer_id = n.benutzer_id
            WHERE n.benutzer_id <> c.fahrer_benutzer_id
              AND NOT n.gelesen
              AND {bedingung}
            ORDER BY n.nachricht_id
            LIMIT 200
            """);
        if (!_ersterNachrichtenLauf)
        {
            befehl.Parameters.Add(new() { Value = _hoechsteNachrichtId });
        }

        var neue = new List<(long Id, long ChatId, string Text, long BenutzerId, string Sprache, string Absender)>();
        await using (var leser = await befehl.ExecuteReaderAsync(abbruch))
        {
            while (await leser.ReadAsync(abbruch))
            {
                var id = leser.GetInt64(0);
                _hoechsteNachrichtId = Math.Max(_hoechsteNachrichtId, id);
                if (_gesendeteNachrichten.Contains(id))
                {
                    continue;
                }
                var sprache = leser.GetString(7);
                // Uebersetzung nur nehmen, wenn sie schon in der Sprache des Fahrers vorliegt (nicht extra anstossen)
                var text = !leser.IsDBNull(3) && leser.GetString(4) == ChatEndpoints.UeUebersetzt
                           && !leser.IsDBNull(5) && leser.GetString(5) == sprache
                    ? leser.GetString(3)
                    : leser.GetString(2);
                var absender = $"{leser.GetString(8)} {leser.GetString(9)}".Trim();
                neue.Add((id, leser.GetInt64(1), text, leser.GetInt64(6), sprache, absender));
            }
        }
        _ersterNachrichtenLauf = false;

        foreach (var n in neue)
        {
            var vorschau = Kuerzen(n.Text.ReplaceLineEndings(" ").Trim());
            var gesendet = await _push.SendenAsync(n.BenutzerId, Texte.NeueNachricht(n.Sprache),
                string.IsNullOrEmpty(n.Absender) ? vorschau : $"{n.Absender}: {vorschau}",
                new Dictionary<string, string>
                {
                    ["typ"] = "nachricht",
                    ["chatId"] = n.ChatId.ToString(CultureInfo.InvariantCulture),
                },
                abbruch);
            // Auch bei Fehlern merken, sonst wird alle 20 Sekunden erneut versucht
            Merken(n.Id);
            if (gesendet)
            {
                _log.LogInformation("Push neue Nachricht {NachrichtId} an Benutzer {BenutzerId}", n.Id, n.BenutzerId);
            }
        }
    }

    private async Task TourenAsync(CancellationToken abbruch)
    {
        await using var befehl = _db.CreateCommand("""
            SELECT t.tour_id, t.fahrer_id, t.tournummer, t.startort, t.zielort, t.startzeit, b.benutzer_id, b.sprache
            FROM public.tour t
            JOIN public.fahrer f ON f.fahrer_id = t.fahrer_id
            JOIN public.benutzer b ON b.benutzer_id = f.benutzer_id
            WHERE t.fahrer_id IS NOT NULL
              AND lower(t.status) NOT SIMILAR TO '%(abgeschlossen|beendet|storniert)%'
            """);

        var aktuell = new HashSet<(long, long)>();
        var neue = new List<(long TourId, string Nummer, string Start, string Ziel, DateTime Startzeit, long BenutzerId, string Sprache)>();
        await using (var leser = await befehl.ExecuteReaderAsync(abbruch))
        {
            while (await leser.ReadAsync(abbruch))
            {
                var paar = (leser.GetInt64(0), leser.GetInt64(1));
                aktuell.Add(paar);
                if (_bekannteTouren is not null && !_bekannteTouren.Contains(paar))
                {
                    neue.Add((paar.Item1, leser.GetString(2), leser.GetString(3), leser.GetString(4),
                        leser.GetDateTime(5), leser.GetInt64(6), leser.GetString(7)));
                }
            }
        }

        var ersterLauf = _bekannteTouren is null;
        _bekannteTouren = aktuell;
        if (ersterLauf)
        {
            return;
        }

        foreach (var t in neue)
        {
            var text = $"{t.Start} → {t.Ziel}, {Texte.Zeit(t.Startzeit, t.Sprache)}";
            if (await _push.SendenAsync(t.BenutzerId, Texte.NeueTour(t.Sprache, t.Nummer), text,
                    new Dictionary<string, string>
                    {
                        ["typ"] = "tour",
                        ["tourId"] = t.TourId.ToString(CultureInfo.InvariantCulture),
                    },
                    abbruch))
            {
                _log.LogInformation("Push neue Tour {TourId} an Benutzer {BenutzerId}", t.TourId, t.BenutzerId);
            }
        }
    }

    private void Merken(long id)
    {
        if (_gesendeteNachrichten.Add(id))
        {
            _gesendeteReihenfolge.Enqueue(id);
        }
        while (_gesendeteReihenfolge.Count > MaxGemerkt)
        {
            _gesendeteNachrichten.Remove(_gesendeteReihenfolge.Dequeue());
        }
    }

    private static string Kuerzen(string text) =>
        text.Length <= MaxVorschau ? text : text[..MaxVorschau].TrimEnd() + "…";

    /// <summary>Texte in der Sprache des Fahrers (de, en, pl; sonst de).</summary>
    private static class Texte
    {
        private static readonly TimeZoneInfo Berlin = TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin");

        public static string NeueNachricht(string sprache) => sprache switch
        {
            "en" => "New message",
            "pl" => "Nowa wiadomość",
            _ => "Neue Nachricht",
        };

        public static string NeueTour(string sprache, string nummer) => sprache switch
        {
            "en" => $"New tour {nummer}",
            "pl" => $"Nowa trasa {nummer}",
            _ => $"Neue Tour {nummer}",
        };

        public static string Zeit(DateTime zeit, string sprache)
        {
            var utc = zeit.Kind == DateTimeKind.Utc ? zeit : DateTime.SpecifyKind(zeit, DateTimeKind.Utc);
            var lokal = TimeZoneInfo.ConvertTimeFromUtc(utc, Berlin);
            return sprache switch
            {
                "en" => lokal.ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture),
                "pl" => lokal.ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture),
                _ => lokal.ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture) + " Uhr",
            };
        }
    }
}
