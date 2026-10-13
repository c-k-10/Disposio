using System;
using Npgsql;

namespace Disposio.Services
{
    internal class Database
    {
        // Hinweis: Connection-String nicht hardcodieren in produktiven Projekten. Hier steht er historisch im Repo.
        private readonly string _connectionString =
            "Host=ep-curly-queen-b2qa2c1e-pooler.c-6.eu-central-1.aws.neon.tech; Database=neondb; Username=neondb_owner; Password=npg_4pc0hjEnJIwk; SSL Mode=VerifyFull; Channel Binding=Require;";

        internal static DatabaseSettings GetSettings()
        {
            // Connection-String in diesem Beispiel: Semikolon-getrennte Schlüssel=Wert Paare.
            var db = new Database();
            var cs = db._connectionString ?? string.Empty;
            var parts = cs.Split(';', StringSplitOptions.RemoveEmptyEntries);
            var dict = new System.Collections.Generic.Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var part in parts)
            {
                var idx = part.IndexOf('=');
                if (idx <= 0) continue;
                var key = part.Substring(0, idx).Trim();
                var value = part.Substring(idx + 1).Trim();
                dict[key] = value;
            }

            dict.TryGetValue("Host", out var host);
            dict.TryGetValue("Database", out var database);
            dict.TryGetValue("Username", out var username);
            dict.TryGetValue("Password", out var password);
            // Port kann optional sein; PostgreSQL-Default ist 5432
            if (!dict.TryGetValue("Port", out var port) || string.IsNullOrEmpty(port))
            {
                // Falls Host mit Port angegeben ist (host:port), trennen
                if (!string.IsNullOrEmpty(host) && host.Contains(':'))
                {
                    var idx = host.LastIndexOf(':');
                    port = host[(idx + 1)..];
                    host = host[..idx];
                }
                else
                {
                    port = "5432";
                }
            }

            return new DatabaseSettings
            {
                Host = host,
                Database = database,
                Username = username,
                Password = password,
                Port = port
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
