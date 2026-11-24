# Desktop-App verteilen und aktualisieren

Die Desktop-App wird als **MSIX-Paket** mit einer **Update-Datei** (`Disposio_x64.appinstaller`) verteilt.
Einen eigenen Server braucht es dafür nicht. Die Dateien liegen auf der Netlify-Seite
**https://disposio-download.netlify.app** (Netlify-Projekt `disposio-download`, Konto von Niklas).
Bei jedem Start fragt Windows dort nach, ob es eine neue Version gibt. Wenn ja, erscheint
„Disposio aktualisieren?“, und die App startet erst nach dem Update (Pflicht-Update).

## Einmalig: Signatur-Zertifikat

Windows installiert nur signierte Pakete. Für das Schulprojekt reicht ein selbst erstelltes Zertifikat.
Sein Name muss genau zum `Publisher` in `src/Disposio/Disposio/Package.appxmanifest` passen (`CN=C K`).

- **Es gibt nur ein Zertifikat für das ganze Team.** Wer Updates baut, braucht genau dieses, sonst
  lehnt Windows das Update ab. Es liegt beim Ersteller im Zertifikatspeicher (Benutzer → Eigene Zertifikate,
  Name „Disposio Paket-Signatur“).
- Weitergeben als `.pfx` mit Passwort, **nie ins Repository**, und Passwort getrennt mitteilen:

```powershell
$zert = Get-ChildItem Cert:\CurrentUser\My | Where-Object FriendlyName -eq 'Disposio Paket-Signatur'
Export-PfxCertificate -Cert $zert -FilePath "$HOME\Desktop\Disposio-Signatur.pfx" -Password (Read-Host -AsSecureString 'Passwort')
```

- Importieren auf einem anderen Entwickler-PC: Doppelklick auf die `.pfx` → „Aktueller Benutzer“ → Passwort.
- Neu erstellen (nur wenn es noch keins gibt; danach müssen alle Nutzer das neue Zertifikat vertrauen):

```powershell
New-SelfSignedCertificate -Type Custom -Subject "CN=C K" -KeyUsage DigitalSignature -FriendlyName "Disposio Paket-Signatur" -CertStoreLocation Cert:\CurrentUser\My -NotAfter (Get-Date).AddYears(3) -TextExtension @("2.5.29.37={text}1.3.6.1.5.5.7.3.3", "2.5.29.19={text}")
```

## Neue Version bauen

Im Repository-Ordner (Visual Studio muss installiert sein):

```powershell
.\tools\DesktopPaketBauen.ps1 -UpdateUrl https://disposio-download.netlify.app/ -Version 1.0.1.0
```

- `-UpdateUrl` ist die Adresse, unter der die Dateien liegen. **Immer genau diese Adresse verwenden**,
  sonst finden installierte Apps keine Updates mehr.
- `-Version` muss bei jedem Update **höher** sein als die letzte (vier Zahlen). Das Skript trägt sie in
  `Package.appxmanifest` ein, diese Änderung mit committen. Beim ersten Mal kann `-Version` wegfallen (1.0.0.0).
- Ergebnis in `src/Disposio/Disposio/AppPackages/` (nicht in Git):
  `Disposio_x64.appinstaller`, `Disposio_<Version>_x64_Test/` (Paket und Windows App Runtime),
  `Disposio.cer`, `_headers` (richtige Dateitypen für Netlify) und `index.html` (deutsche Download-Seite
  aus `tools/Downloadseite.html`).

## Hochladen

Mit dem Netlify-Werkzeug (einmalig `npx netlify-cli login`, dann im Browser „Authorize“):

```powershell
npx netlify-cli deploy --prod --no-build --dir src\Disposio\Disposio\AppPackages --site disposio-download
```

Oder im Browser: bei Netlify anmelden → Projekt **disposio-download** → **Activity** → **Upload a new version**
→ den **ganzen Ordner** `src/Disposio/Disposio/AppPackages` auswählen (nicht einen Unterordner).

Danach prüfen: https://disposio-download.netlify.app/Disposio_x64.appinstaller zeigt die neue Versionsnummer.

Netlify ersetzt bei jedem Upload die ganze Seite. Deshalb immer den ganzen Ordner hochladen.
Der Ordner liegt nur lokal bei der Person, die baut. Alte Versionen braucht niemand, die Update-Datei zeigt immer auf die neueste.

Neue Netlify-Projekte sind zuerst **Private** (dann antwortet die Seite mit 401, und Windows findet keine
Updates). Bei `disposio-download` ist unter Manage → General → Visitor access „Public“ eingestellt.

## Auf einem neuen PC installieren

Für Kollegen: einfach https://disposio-download.netlify.app öffnen, die Seite erklärt die Schritte.

1. https://disposio-download.netlify.app/Disposio.cer herunterladen, Doppelklick → „Zertifikat installieren“ → **Lokaler Computer** →
   „Alle Zertifikate in folgendem Speicher speichern“ → **Vertrauenswürdige Personen**. Braucht einmal
   Admin-Rechte.
2. https://disposio-download.netlify.app/Disposio_x64.appinstaller herunterladen und doppelklicken →
   „Installieren“. (Ein `ms-appinstaller:`-Link zum Direkt-Installieren funktioniert nicht, den hat
   Windows seit Ende 2023 standardmäßig gesperrt. Deshalb die Datei herunterladen.)
   Wichtig: über diese Datei installieren, nicht über die `.msix`. Nur dann kommen Updates an.
3. Die Benutzervariablen setzen wie in [Disposio-lokal-starten](Disposio-lokal-starten.md)
   (`DISPOSIO_CONNECTION_STRING`, bei Bedarf `ANTHROPIC_API_KEY` usw.). Ohne sie startet die App, aber
   Datenbankzugriffe schlagen fehl.

## Gut zu wissen

- Nach „Aktualisieren“ startet Disposio nicht von selbst. Einfach neu öffnen.
- Bleibt das Fenster „App-Installer“ leer oder reagiert nicht, das Fenster schließen und Disposio neu starten.
- Ein PC mit Visual Studio kann nicht gleichzeitig die installierte und die Entwicklungsversion haben
  (gleicher Paketname). Vor dem Start aus Visual Studio die installierte Version unter Einstellungen → Apps deinstallieren.
- **Sicherheit:** Auf jedem PC liegt der volle Datenbank-Zugang als Umgebungsvariable. Für Vorführungen auf
  eigenen Rechnern ist das in Ordnung. Für echte Kunden müsste die App über das Backend (`Disposio.Api`) laufen.
