#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Rewrites each case's expected files from the C# app, the reference implementation (spec/testdata.md).
.DESCRIPTION
    Run it after a deliberate change to the analysis or the report, check the differences with git diff, and
    commit them with the change. The Python port must then match the new files (or the PR opens a parity issue).
#>
param([string[]] $Case)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$project = Join-Path $root '..' 'src' 'OvenTracker'
dotnet build $project -c Release -v q --nologo | Out-Host
if ($LASTEXITCODE -ne 0) { throw 'build failed' }
$dll = Get-ChildItem (Join-Path $project 'bin' 'Release') -Recurse -Filter OvenTracker.dll | Where-Object { $_.Directory.Name -like 'net*' } | Select-Object -First 1
$env:NO_COLOR = '1'
foreach ($dir in Get-ChildItem (Join-Path $root 'cases') -Directory | Sort-Object Name) {
    if ($Case -and $dir.Name -notin $Case) { continue }
    $argv = @(Get-Content (Join-Path $dir 'args.txt') | ForEach-Object { $_.Trim() } | Where-Object { $_ -and -not $_.StartsWith('#') } |
        ForEach-Object { if ($_.StartsWith('runs/')) { Join-Path $root $_ } else { $_ } })
    $expected = Join-Path $dir 'expected'
    if (Test-Path $expected) { Remove-Item $expected -Recurse -Force }
    dotnet $dll.FullName analyze @argv --out $expected | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "analyze failed for $($dir.Name)" }
    Write-Host "$($dir.Name): $((Get-ChildItem $expected).Name -join ', ')"
}
