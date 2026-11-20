<#
.SYNOPSIS
    Tests, publishes, signs and zips OvenTracker for Windows.

.DESCRIPTION
    1. Runs the tests (unless -SkipTests).
    2. Publishes src\OvenTracker for win-x64 into artifacts\publish: framework-dependent by default
       (needs the .NET 11 runtime), or with -SelfContained, everything included.
    3. With -SignScript, signs OvenTracker's own programs (OvenTracker.exe and OvenTracker.dll) and
       checks each one is validly signed, failing the build if not. Files that already carry a valid
       signature are left alone, and so are other publishers' DLLs (Spectre.Console, Microsoft's).
       The signing script is given the files, and must sign and timestamp them and fail if it cannot.
       The signing account and its tools live outside this repository, so the script's path is given
       here, or in OVENTRACKER_SIGN_SCRIPT, never written into it. Without one the build is unsigned,
       and says so. ManualForge (tools\build-installer.ps1) and GPIB-MCP (packaging\Sign-OwnFiles.ps1)
       sign the same way.
    4. Zips the signed folder into artifacts\OvenTracker-<version>-win-x64.zip.

.EXAMPLE
    ./build.ps1 -SignScript C:\signing\Sign-Files.ps1
#>
[CmdletBinding()]
param(
    [string] $Configuration = 'Release',
    [string] $Runtime = 'win-x64',
    [switch] $SelfContained,
    [switch] $SkipTests,
    [string] $SignScript = $env:OVENTRACKER_SIGN_SCRIPT
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$artifacts = Join-Path $root 'artifacts'
$publish = Join-Path $artifacts 'publish'
$project = Join-Path $root 'src\OvenTracker\OvenTracker.csproj'

function Write-Step([string] $text) { Write-Host "==> $text" -ForegroundColor Cyan }

if (-not $SkipTests) {
    Write-Step 'Testing'
    dotnet test (Join-Path $root 'OvenTracker.slnx') -c $Configuration --nologo
    if ($LASTEXITCODE -ne 0) { throw "Tests failed ($LASTEXITCODE)." }
}

Write-Step "Publishing ($Runtime, $(if ($SelfContained) { 'self-contained' } else { 'framework-dependent' }))"
if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
dotnet publish $project -c $Configuration -r $Runtime --self-contained:$($SelfContained.IsPresent) -o $publish --nologo -p:DebugType=none
if ($LASTEXITCODE -ne 0) { throw "Publish failed ($LASTEXITCODE)." }

# OvenTracker's own programs only: everyone else's DLLs are theirs to sign.
$own = @(Get-ChildItem $publish -File | Where-Object { $_.Name -match '^OvenTracker\.(exe|dll)$' } | ForEach-Object FullName)
if ($own.Count -ne 2) { throw "Expected OvenTracker.exe and OvenTracker.dll in $publish, found: $($own -join ', ')" }

if ($SignScript) {
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

$version = (Get-Item (Join-Path $publish 'OvenTracker.dll')).VersionInfo.ProductVersion -replace '\+.*$', ''
$suffix = if ($SelfContained) { "$Runtime-self-contained" } else { $Runtime }
$zip = Join-Path $artifacts "OvenTracker-$version-$suffix.zip"
Write-Step "Packing $([IO.Path]::GetFileName($zip))"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path (Join-Path $publish '*') -DestinationPath $zip
Write-Host "   $zip ($([math]::Round((Get-Item $zip).Length / 1MB, 1)) MB)"
