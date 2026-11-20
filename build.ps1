#Requires -Version 7
<#
.SYNOPSIS
    Tests, publishes, signs and packs OvenTracker for Windows and Linux.

.DESCRIPTION
    Runs under PowerShell 7 on Windows or Linux.

    1. Runs the tests (unless -SkipTests).
    2. Publishes src/OvenTracker for each runtime in -Runtime into artifacts/publish/<runtime>:
       framework-dependent by default (needs the .NET 11 runtime), or with -SelfContained, everything
       included. The default runtime is the one this machine runs: win-x64 or linux-x64.
    3. For a Windows runtime, with -SignScript, signs OvenTracker's own programs (OvenTracker.exe and
       OvenTracker.dll) and checks each one is validly signed, failing the build if not. Files that
       already carry a valid signature are left alone, and so are other publishers' DLLs
       (Spectre.Console, Microsoft's). The signing script is given the files, and must sign and
       timestamp them and fail if it cannot. The signing account and its tools live outside this
       repository, so the script's path is given here, or in OVENTRACKER_SIGN_SCRIPT, never written
       into it; the release workflow passes packaging/Sign-ArtifactSigning.ps1. Without one the build is
       unsigned, and says so. Signing needs Windows; Linux programs are not Authenticode-signed.
    4. Packs each runtime: artifacts/OvenTracker-<version>-<runtime>.zip for Windows, and .tar.gz for
       Linux, which keeps the program's executable bit (so build Linux packages on Linux).

.EXAMPLE
    ./build.ps1 -SignScript C:\signing\Sign-Files.ps1

.EXAMPLE
    ./build.ps1 -Runtime linux-x64,linux-arm64 -SelfContained
#>
[CmdletBinding()]
param(
    [string] $Configuration = 'Release',
    [string[]] $Runtime = @($(if ($IsWindows) { 'win-x64' } else { 'linux-x64' })),
    [switch] $SelfContained,
    [switch] $SkipTests,
    [string] $Version,
    [string] $SignScript = $env:OVENTRACKER_SIGN_SCRIPT
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$artifacts = Join-Path $root 'artifacts'
$project = Join-Path $root 'src' 'OvenTracker' 'OvenTracker.csproj'
$versionArgs = if ($Version) { @("-p:Version=$Version") } else { @() }

function Write-Step([string] $text) { Write-Host "==> $text" -ForegroundColor Cyan }

if (-not $SkipTests) {
    Write-Step 'Testing'
    dotnet test (Join-Path $root 'OvenTracker.slnx') -c $Configuration --nologo @versionArgs
    if ($LASTEXITCODE -ne 0) { throw "Tests failed ($LASTEXITCODE)." }
}

foreach ($rid in $Runtime) {
    $isWindowsRid = $rid -like 'win-*'
    $publish = Join-Path $artifacts 'publish' $rid
    Write-Step "Publishing $rid ($(if ($SelfContained) { 'self-contained' } else { 'framework-dependent' }))"
    if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
    dotnet publish $project -c $Configuration -r $rid --self-contained:$($SelfContained.IsPresent) -o $publish --nologo -p:DebugType=none @versionArgs
    if ($LASTEXITCODE -ne 0) { throw "Publish failed for $rid ($LASTEXITCODE)." }

    # OvenTracker's own programs only: everyone else's DLLs are theirs to sign.
    $program = if ($isWindowsRid) { 'OvenTracker.exe' } else { 'OvenTracker' }
    $own = @($program, 'OvenTracker.dll' | ForEach-Object { Join-Path $publish $_ })
    $missing = @($own | Where-Object { -not (Test-Path $_) })
    if ($missing) { throw "Expected $program and OvenTracker.dll in $publish; missing: $($missing -join ', ')" }

    if ($isWindowsRid) {
        if ($SignScript -and -not $IsWindows) {
            Write-Warning "Not signed: signing $rid needs Windows."
        }
        elseif ($SignScript) {
            if (-not (Test-Path $SignScript)) { throw "No signing script at $SignScript." }
            Write-Step 'Signing OvenTracker''s own programs'
            $toSign = @($own | Where-Object { (Get-AuthenticodeSignature $_).Status -ne 'Valid' })
            if ($toSign) {
                & $SignScript @toSign
                if (-not $?) { throw 'The signing script failed.' }
            }
            $unsigned = @($own | Where-Object { (Get-AuthenticodeSignature $_).Status -ne 'Valid' })
            if ($unsigned) { throw "Still not validly signed: $($unsigned -join ', ')" }
            Write-Host ("   {0} file(s), signed by {1}" -f $own.Count, (Get-AuthenticodeSignature $own[0]).SignerCertificate.Subject)
        }
        else {
            Write-Warning 'Not signed: no -SignScript and no OVENTRACKER_SIGN_SCRIPT.'
        }
    }
    elseif ($IsWindows) {
        Write-Warning "$rid packed on Windows: tar can't mark the program executable from here, so after unpacking run 'chmod +x OvenTracker', or build on Linux."
    }

    $packVersion = if ($Version) { $Version } else { (Get-Item (Join-Path $publish 'OvenTracker.dll')).VersionInfo.ProductVersion -replace '\+.*$', '' }
    $suffix = if ($SelfContained) { "$rid-self-contained" } else { $rid }
    $archive = Join-Path $artifacts "OvenTracker-$packVersion-$suffix$(if ($isWindowsRid) { '.zip' } else { '.tar.gz' })"
    Write-Step "Packing $([IO.Path]::GetFileName($archive))"
    if (Test-Path $archive) { Remove-Item $archive -Force }
    if ($isWindowsRid) {
        Compress-Archive -Path (Join-Path $publish '*') -DestinationPath $archive
    }
    else {
        tar -czf $archive -C $publish .
        if ($LASTEXITCODE -ne 0) { throw "tar failed for $rid ($LASTEXITCODE)." }
    }
    Write-Host "   $archive ($([math]::Round((Get-Item $archive).Length / 1MB, 1)) MB)"
}
