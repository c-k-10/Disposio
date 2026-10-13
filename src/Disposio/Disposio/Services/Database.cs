using System;
using Npgsql;

namespace Disposio.Services
{
    internal class Database
    {
        internal const string ConnectionStringEnvironmentVariable = "DISPOSIO_CONNECTION_STRING";

        // Eine gemeinsame lokale Konfiguration für HTTPS und direkte PostgreSQL-Verbindungen.
        // Zugangsdaten werden weder im Quellcode noch in Fehlermeldungen gespeichert.
        private readonly string _connectionString = ReadConnectionString().ConnectionString;

        private static NpgsqlConnectionStringBuilder ReadConnectionString()
        {
            var value = Environment.GetEnvironmentVariable(ConnectionStringEnvironmentVariable);
            if (string.IsNullOrWhiteSpace(value))
                throw new InvalidOperationException("Die lokale Datenbankkonfiguration DISPOSIO_CONNECTION_STRING fehlt.");

            NpgsqlConnectionStringBuilder settings;
            try
            {
                // Der offizielle Parser unterstützt auch Aliase und korrekt zitierte Sonderzeichen.
                settings = new NpgsqlConnectionStringBuilder(value);
            }
            catch (Exception exception) when (exception is ArgumentException or FormatException or OverflowException)
            {
                // Parserfehler können Teile der Eingabe enthalten: keine Details weitergeben.
                throw new InvalidOperationException("Die lokale Datenbankkonfiguration ist ungültig.");
            }

            if (string.IsNullOrWhiteSpace(settings.Host) || string.IsNullOrWhiteSpace(settings.Database) ||
                string.IsNullOrWhiteSpace(settings.Username) || string.IsNullOrEmpty(settings.Password))
                throw new InvalidOperationException("Die lokale Datenbankkonfiguration benötigt Host, Database, Username und Password.");

            return settings;
        }

        internal static DatabaseSettings GetSettings()
        {
            var settings = ReadConnectionString();
            return new DatabaseSettings
            {
                Host = settings.Host,
                Database = settings.Database,
                Username = settings.Username,
                Password = settings.Password,
                Port = settings.Port.ToString(System.Globalization.CultureInfo.InvariantCulture)
            };
        }

        public NpgsqlConnection GetConnection()
        {
            var conn = new NpgsqlConnection(_connectionString);
            conn.Open();
            return conn;
        }
    }

    internal sealed class DatabaseSettings
    {
        public string? Host { get; init; }
        public string? Username { get; init; }
        public string? Database { get; init; }
        public string? Password { get; init; }
        public string? Port { get; init; }
    }
}
