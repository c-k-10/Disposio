# Disposio im Entwicklerteam verbinden

Diese Anleitung gilt für den Entwicklerstand nach Übernahme des Neon-Format-Fixes.
Die Kundenarchitektur mit zentralem Backend ist eine getrennte Aufgabe.

1. Aktuellen `main`-Stand holen. Eigene Codeänderungen vorher sichern.
2. In Neon das Projekt Disposio öffnen, auf **Connect** klicken und die vollständige
   PostgreSQL-Verbindung kopieren. Kein neues Passwort erzeugen.
3. Windows-Suche: **Umgebungsvariablen für dieses Konto bearbeiten**.
   Unter **Benutzervariablen** den Eintrag `DISPOSIO_CONNECTION_STRING` erstellen oder bearbeiten.
   Als Wert die vollständige kopierte Verbindung einsetzen.
4. Alle Dialoge mit OK schließen. Visual Studio vollständig schließen und neu öffnen.
5. Mit **Debug / x64 / Disposio (Package)** starten.

Die App akzeptiert jetzt `postgresql://`, `postgres://`, das bisherige Npgsql-Format
und eine vollständig kopierte Zeile der Form `psql '...'`. Das Passwort allein genügt nicht.
Es gibt keine neue Einrichtungsseite und keine Änderung an Login oder Dashboard.
Zugangsdaten nicht in Git, Chat oder Screenshots veröffentlichen.

Falls das Format weiterhin abgelehnt wird, kann aus dem Repository eine lokale Prüfung
laufen. Sie gibt keine Zugangsdaten aus und versucht keine Datenbankverbindung:

```powershell
dotnet run --project tests/Disposio.Configuration.Tests/Disposio.Configuration.Tests.csproj -- --check-local
```

Ein erfolgreiches Format-Ergebnis bestätigt noch nicht Netzwerkzugriff, Datenbankrechte oder Schema.
