#Requires -Version 7
<#
.SYNOPSIS
    Builds OvenTracker's Windows installer (Inno Setup) from the published app, signed when a signing script is given.

.DESCRIPTION
    Run after ./build.ps1 -Runtime win-x64, which publishes the framework-dependent app to
    artifacts/publish/win-x64 and signs OvenTracker's own programs. This compiles packaging/OvenTracker.iss into
    artifacts/OvenTracker-<version>-win-x64-setup.exe.

    With -SignScript (or OVENTRACKER_SIGN_SCRIPT), Inno Setup signs the installer and its uninstaller with it, and
    this checks the installer, and the programs it installs, are validly signed, failing if not. The script is the
    same one build.ps1 takes: it's given file paths, and must sign and timestamp them. Without one the installer is
    unsigned, and this says so.

    Inno Setup 6 must be installed (ISCC.exe in Program Files, %LOCALAPPDATA%\Programs, or on PATH).

.EXAMPLE
    ./build.ps1 -Runtime win-x64 -SignScript C:\signing\Sign-Files.ps1
    ./packaging/build-installer.ps1 -SignScript C:\signing\Sign-Files.ps1
#>
[CmdletBinding()]
param(
    [string] $Version,
    [string] $SourceDir,
    [string] $SignScript = $env:OVENTRACKER_SIGN_SCRIPT
)

$ErrorActionPreference = 'Stop'
if (-not $IsWindows) { throw 'The Windows installer is built on Windows.' }
$root = Split-Path $PSScriptRoot -Parent
$artifacts = Join-Path $root 'artifacts'
if (-not $SourceDir) { $SourceDir = Join-Path $artifacts 'publish' 'win-x64' }
$exe = Join-Path $SourceDir 'OvenTracker.exe'
if (-not (Test-Path $exe)) { throw "No published app at $SourceDir; run ./build.ps1 -Runtime win-x64 first." }
if (Test-Path (Join-Path $SourceDir 'coreclr.dll')) { throw "$SourceDir holds a self-contained build; the installer takes the framework-dependent one (it installs the runtime itself)." }
if (-not $Version) { $Version = (Get-Item (Join-Path $SourceDir 'OvenTracker.dll')).VersionInfo.ProductVersion -replace '\+.*$', '' }

$iscc = @(
    (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
    (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe'),
    (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe')
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { $iscc = (Get-Command ISCC.exe -ErrorAction SilentlyContinue)?.Source }
if (-not $iscc) { throw 'Inno Setup 6 is not installed (winget install JRSoftware.InnoSetup, or choco install innosetup).' }

$own = @($exe, (Join-Path $SourceDir 'OvenTracker.dll'))
$isccArgs = @("/DAppVersion=$Version", "/DSourceDir=$SourceDir", "/DOutputDir=$artifacts", '/Qp')
if ($SignScript) {
    if (-not (Test-Path $SignScript)) { throw "No signing script at $SignScript." }
    $notSigned = @($own | Where-Object { (Get-AuthenticodeSignature $_).Status -ne 'Valid' })
    if ($notSigned) { throw "The programs to install aren't signed ($($notSigned -join ', ')); build with ./build.ps1 -SignScript first." }
    $pwsh = (Get-Process -Id $PID).Path
    $script = (Resolve-Path $SignScript).Path
    # Inno Setup runs this for the uninstaller and the installer, with $f the file and $q a quote.
    $isccArgs += '/DSign', "/Soventracker=`$q$pwsh`$q -NoProfile -ExecutionPolicy Bypass -File `$q$script`$q `$f"
}
else {
    Write-Warning 'The installer is not signed: no -SignScript and no OVENTRACKER_SIGN_SCRIPT.'
}

Write-Host "==> Building the installer, OvenTracker $Version" -ForegroundColor Cyan
& $iscc @isccArgs (Join-Path $PSScriptRoot 'OvenTracker.iss')
if ($LASTEXITCODE -ne 0) { throw "Inno Setup failed ($LASTEXITCODE)." }

$setup = Join-Path $artifacts "OvenTracker-$Version-win-x64-setup.exe"
if (-not (Test-Path $setup)) { throw "Inno Setup made no $setup." }
if ($SignScript) {
    $signature = Get-AuthenticodeSignature $setup
    if ($signature.Status -ne 'Valid') { throw "The installer isn't validly signed: $($signature.Status) $($signature.StatusMessage)" }
    Write-Host "   signed by $($signature.SignerCertificate.Subject)"
}
Write-Host "   $setup ($([math]::Round((Get-Item $setup).Length / 1MB, 1)) MB)"
