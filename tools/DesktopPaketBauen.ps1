<#
.SYNOPSIS
  Baut die Disposio-Desktop-App als signiertes MSIX-Paket mit Update-Datei (.appinstaller).

.DESCRIPTION
  Ergebnis liegt in src\Disposio\Disposio\AppPackages (nicht in Git):
    Disposio_x64.appinstaller        Update-Datei. Diese Datei oeffnen Nutzer zum Installieren.
    Disposio_<Version>_x64_Test\     Paket (.msix) und Windows App Runtime (Dependencies\x64)
    Disposio.cer                     oeffentlicher Teil des Zertifikats (einmal je PC vertrauen)
    _headers                         Dateitypen fuer Netlify
    index.html                       einfache Download-Seite
  Den ganzen Ordner AppPackages als Netlify-Seite unter -UpdateUrl veroeffentlichen.
  Anleitung: docs\00_Admin\Desktop-App-verteilen.md

.PARAMETER UpdateUrl
  Ordner-Adresse, unter der die Dateien spaeter liegen, z. B. https://disposio-download.netlify.app/
  Die App fragt bei jedem Start dort nach einer neuen Version.

.PARAMETER Version
  Neue Versionsnummer (vier Zahlen, z. B. 1.0.1.0). Wird in Package.appxmanifest eingetragen.
  Ohne Angabe gilt die Version, die dort schon steht.

.EXAMPLE
  .\tools\DesktopPaketBauen.ps1 -UpdateUrl https://disposio-download.netlify.app/ -Version 1.0.1.0
#>
param(
    [Parameter(Mandatory = $true)] [string] $UpdateUrl,
    [string] $Version
)

$ErrorActionPreference = 'Stop'

$repo = Split-Path $PSScriptRoot -Parent
$projekt = Join-Path $repo 'src\Disposio\Disposio'
$manifestPfad = Join-Path $projekt 'Package.appxmanifest'
$ausgabe = Join-Path $projekt 'AppPackages\'
if (-not ($UpdateUrl.EndsWith('/') -or $UpdateUrl.EndsWith('\'))) { $UpdateUrl += '/' }

# 1. Version lesen bzw. setzen (nur das Version-Attribut im Identity-Element)
$manifest = [IO.File]::ReadAllText($manifestPfad)
$identityMuster = '(?s)(<Identity\b[^>]*?\bVersion=")([^"]+)(")'
if ($manifest -notmatch $identityMuster) { throw "Keine Version im Identity-Element von $manifestPfad gefunden." }
$alteVersion = $Matches[2]
if ($Version) {
    if ($Version -notmatch '^\d+\.\d+\.\d+\.\d+$') { throw "Version '$Version' ungueltig. Erwartet vier Zahlen, z. B. 1.0.1.0." }
    if ([version]$Version -lt [version]$alteVersion) { throw "Version $Version ist kleiner als die aktuelle $alteVersion." }
    $manifest = [regex]::Replace($manifest, $identityMuster, { param($m) $m.Groups[1].Value + $Version + $m.Groups[3].Value }, 'None', [TimeSpan]::FromSeconds(1))
    [IO.File]::WriteAllText($manifestPfad, $manifest, (New-Object Text.UTF8Encoding $true))
} else {
    $Version = $alteVersion
}
if (Test-Path (Join-Path $ausgabe "Disposio_$($Version)_x64_Test")) {
    throw "Version $Version wurde schon gebaut. Fuer ein Update mit -Version eine hoehere Nummer angeben."
}

# 2. Zertifikat suchen: gleicher Name wie Publisher im Manifest, mit privatem Schluessel
if ($manifest -notmatch '(?s)<Identity\b[^>]*?\bPublisher="([^"]+)"') { throw "Kein Publisher in $manifestPfad gefunden." }
$publisher = $Matches[1]
$zertifikat = Get-ChildItem Cert:\CurrentUser\My |
    Where-Object { $_.Subject -eq $publisher -and $_.HasPrivateKey -and $_.NotAfter -gt (Get-Date) -and
                   ($_.EnhancedKeyUsageList.ObjectId -contains '1.3.6.1.5.5.7.3.3') } |
    Sort-Object NotAfter -Descending | Select-Object -First 1
if (-not $zertifikat) {
    throw "Kein gueltiges Signatur-Zertifikat '$publisher' in Cert:\CurrentUser\My. Siehe docs\00_Admin\Desktop-App-verteilen.md, Abschnitt Zertifikat."
}

# 3. MSBuild von Visual Studio suchen (dotnet build scheitert am XAML-Compiler)
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$msbuild = & $vswhere -prerelease -latest -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
if (-not $msbuild) { throw 'MSBuild nicht gefunden. Ist Visual Studio installiert?' }

Write-Host "Baue Disposio $Version (Zertifikat $($zertifikat.Thumbprint), Update-Adresse $UpdateUrl) ..."
& $msbuild (Join-Path $projekt 'Disposio.csproj') -restore -nologo -v:m `
    -p:Configuration=Release -p:Platform=x64 `
    -p:GenerateAppxPackageOnBuild=true -p:UapAppxPackageBuildMode=SideloadOnly -p:AppxBundle=Never `
    -p:AppxPackageSigningEnabled=true "-p:PackageCertificateThumbprint=$($zertifikat.Thumbprint)" `
    -p:GenerateAppInstallerFile=true "-p:AppInstallerUri=$UpdateUrl" -p:HoursBetweenUpdateChecks=0 `
    "-p:AppxPackageDir=$ausgabe"
if ($LASTEXITCODE -ne 0) { throw "Build fehlgeschlagen (MSBuild-Code $LASTEXITCODE)." }

# 4. Oeffentlichen Teil des Zertifikats neben die Update-Datei legen
Export-Certificate -Cert $zertifikat -FilePath (Join-Path $ausgabe 'Disposio.cer') | Out-Null

# 5. Netlify: richtige Dateitypen fuer Update-Datei und Pakete (Ordner = Wurzel der Download-Seite)
$headers = "/Disposio_x64.appinstaller`n  Content-Type: application/appinstaller`n/*.msix`n  Content-Type: application/msix`n"
[IO.File]::WriteAllText((Join-Path $ausgabe '_headers'), $headers)

# 6. Nur die x64-Laufzeit wird gebraucht (steht so in der Update-Datei), Rest weg = kleinerer Upload
Get-ChildItem (Join-Path $ausgabe "Disposio_$($Version)_x64_Test\Dependencies") -Directory |
    Where-Object Name -ne 'x64' | Remove-Item -Recurse -Force

Write-Host ''
Write-Host "Fertig: Disposio $Version liegt in $ausgabe"
Write-Host "Den Ordnerinhalt nach $UpdateUrl hochladen. Installieren: $($UpdateUrl)Disposio_x64.appinstaller"
