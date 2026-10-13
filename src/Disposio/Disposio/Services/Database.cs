using System;
using System.Threading;
using System.Threading.Tasks;
using Npgsql;

namespace Disposio.Services;

/// <summary>Gemeinsamer Einstiegspunkt für Verbindungen zur vorhandenen Neon-Datenbank.</summary>
internal sealed class Database
{
    // Die vorhandene Verbindung bleibt der Standard. Der Wert wird nicht in Fehlermeldungen angezeigt.
    // Optional ersetzt DISPOSIO_DB_CONNECTION_STRING ihn, z. B. auf einem anderen Entwicklungsrechner.
    private const string ExistingConnectionString = "Host=ep-curly-queen-b2qa2c1e-pooler.c-6.eu-central-1.aws.neon.tech; Database=neondb; Username=neondb_owner; Password=npg_4pc0hjEnJIwk; SSL Mode=VerifyFull; Channel Binding=Require;";

    // Eine DataSource verwaltet wiederverwendbare Verbindungen (Connection-Pooling).
    // Lazy erstellt sie erst beim ersten Datenbankzugriff; alle Repository-Aufrufe teilen dieselbe Instanz.
    private static readonly Lazy<NpgsqlDataSource> DataSource = new(() => NpgsqlDataSource.Create(GetSettings().ConnectionString));

    // Beide Transportwege verwenden dieselben Zugangsdaten: Npgsql spricht direkt PostgreSQL,
    // NeonSqlClient schickt SQL über Neons offiziellen HTTPS-Zugang an dieselbe Datenbank.
    internal static NpgsqlConnectionStringBuilder GetSettings()
    {
        var overrideConnection = Environment.GetEnvironmentVariable("DISPOSIO_DB_CONNECTION_STRING");
        var connectionString = string.IsNullOrWhiteSpace(overrideConnection)
            ? ExistingConnectionString
            : overrideConnection;
        // Neon kann nach einer Ruhephase kurz zum Aufwachen brauchen. Nach 30 Sekunden brechen wir ab.
        // Der Builder übernimmt alle vorhandenen TLS-/Channel-Binding-Einstellungen unverändert.
        var settings = new NpgsqlConnectionStringBuilder(connectionString)
        {
            Timeout = 30,
            CommandTimeout = 30
        };
        return settings;
    }

    // Die Oberfläche verwendet den asynchronen Weg, damit eine langsame Internetverbindung sie nicht einfriert.
    // Der Aufrufer muss die Verbindung mit await using wieder freigeben: Sie geht dann zurück in den Pool.
    public static ValueTask<NpgsqlConnection> OpenConnectionAsync(CancellationToken cancellationToken = default) =>
        DataSource.Value.OpenConnectionAsync(cancellationToken);

    // Dieser bisherige Einstieg bleibt für vorhandenen Code verfügbar. Neuer UI-Code nutzt OpenConnectionAsync.
    public NpgsqlConnection GetConnection() => DataSource.Value.OpenConnection();
}
