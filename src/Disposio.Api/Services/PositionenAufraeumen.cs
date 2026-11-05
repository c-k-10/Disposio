using Npgsql;

namespace Disposio.Api.Services;

/// <summary>
/// Phase 3: loescht GPS-Positionen, die aelter als 30 Tage sind.
/// Laeuft beim Start der API und danach einmal taeglich.
/// </summary>
public sealed class PositionenAufraeumen : BackgroundService
{
    private static readonly TimeSpan Aufbewahrung = TimeSpan.FromDays(30);

    private readonly NpgsqlDataSource _db;
    private readonly ILogger<PositionenAufraeumen> _log;

    public PositionenAufraeumen(NpgsqlDataSource db, ILogger<PositionenAufraeumen> log)
    {
        _db = db;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken abbruch)
    {
        using var takt = new PeriodicTimer(TimeSpan.FromDays(1));
        do
        {
            try
            {
                await using var befehl = _db.CreateCommand(
                    "DELETE FROM public.gps_position WHERE erfasst_am < now() - $1::interval");
                befehl.Parameters.Add(new() { Value = Aufbewahrung });
                var geloescht = await befehl.ExecuteNonQueryAsync(abbruch);
                _log.LogInformation("{Anzahl} alte GPS-Positionen geloescht", geloescht);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogWarning(ex, "Alte GPS-Positionen konnten nicht geloescht werden");
            }
        }
        while (await takt.WaitForNextTickAsync(abbruch));
    }
}
