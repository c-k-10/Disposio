using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
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

            return ParseConnectionString(value);
        }

        // Beide Formate werden über dieselbe Prüfung geführt. Keine Eingabe in Fehlermeldungen.
        internal static NpgsqlConnectionStringBuilder ParseConnectionString(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                throw new InvalidOperationException("Bitte die vollständige Datenbankverbindung einfügen.");
            NpgsqlConnectionStringBuilder settings;
            try
            {
                value = value.Trim();
                if (value.StartsWith("psql ", StringComparison.OrdinalIgnoreCase))
                {
                    // Nur Text extrahieren. Niemals eine kopierte Konsolenzeile ausführen.
                    value = value[5..].Trim();
                    if (value.Length < 2 || (value[0] != '\'' && value[0] != '"') || value[^1] != value[0])
                        throw new FormatException();
                    value = value[1..^1];
                    if (!value.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase) &&
                        !value.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase))
                        throw new FormatException();
                }
                else if (value.Length >= 2 && (value[0] == '\'' || value[0] == '"') && value[^1] == value[0])
                {
                    value = value[1..^1].Trim();
                }
                if (value.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase) ||
                    value.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase))
                {
                    if (Regex.IsMatch(value, @"%(?![0-9A-Fa-f]{2})")) throw new FormatException();
                    var uri = new Uri(value, UriKind.Absolute);
                    var identity = uri.UserInfo.Split(':', 2);
                    if (identity.Length != 2 || uri.Fragment.Length != 0 || uri.AbsolutePath.Length <= 1 || uri.Port == 0)
                        throw new FormatException();
                    settings = new NpgsqlConnectionStringBuilder
                    {
                        Host = uri.DnsSafeHost,
                        Database = Uri.UnescapeDataString(uri.AbsolutePath[1..]),
                        Username = Uri.UnescapeDataString(identity[0]),
                        Password = Uri.UnescapeDataString(identity[1]),
                        Port = uri.Port > 0 ? uri.Port : 5432,
                        SslMode = SslMode.VerifyFull
                    };
                    // Neons übliche URL-Optionen: Keine unbemerkte Übernahme unbekannter Optionen.
                    var seenOptions = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var option in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
                    {
                        var pair = option.Split('=', 2);
                        var key = Uri.UnescapeDataString(pair[0]);
                        if (!seenOptions.Add(key)) throw new FormatException();
                        var optionValue = pair.Length == 2 ? Uri.UnescapeDataString(pair[1]) : "";
                        if (key == "sslmode")
                        {
                            if (optionValue is not ("require" or "verify-full")) throw new FormatException();
                        }
                        else if (key == "channel_binding")
                        {
                            if (optionValue is not ("require" or "prefer" or "disable")) throw new FormatException();
                            settings["Channel Binding"] = optionValue;
                        }
                        else throw new FormatException();
                    }
                }
                else settings = new NpgsqlConnectionStringBuilder(value);
            }
            catch (Exception exception) when (exception is ArgumentException or FormatException or OverflowException)
            {
                // Parserfehler können Teile der Eingabe enthalten: keine Details weitergeben.
                throw new InvalidOperationException("Die lokale Datenbankkonfiguration ist ungültig. Bitte die vollständige Neon-Verbindung (postgresql:// oder Host=...) einfügen, nicht nur das Passwort.");
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
