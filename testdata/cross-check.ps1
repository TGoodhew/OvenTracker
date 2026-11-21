#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Runs the C# app's and the Python port's analyze over every case in testdata/cases, and compares the two
    outputs with each other and with the expected files (spec/testdata.md). CI runs it; it also runs locally.
.PARAMETER Python
    The Python to use, with the oventracker package installed (pip install ./python).
#>
param([string] $Python = 'python', [string] $Out = (Join-Path ([IO.Path]::GetTempPath()) "oventracker-crosscheck"))
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$project = Join-Path $root '..' 'src' 'OvenTracker'
dotnet build $project -c Release -v q --nologo | Out-Host
if ($LASTEXITCODE -ne 0) { throw 'build failed' }
$dll = Get-ChildItem (Join-Path $project 'bin' 'Release') -Recurse -Filter OvenTracker.dll | Where-Object { $_.Directory.Name -like 'net*' } | Select-Object -First 1
$compare = Join-Path $root 'tools' 'compare.py'
$env:NO_COLOR = '1'
if (Test-Path $Out) { Remove-Item $Out -Recurse -Force }
$failed = @()
foreach ($dir in Get-ChildItem (Join-Path $root 'cases') -Directory | Sort-Object Name) {
    $argv = @(Get-Content (Join-Path $dir 'args.txt') | ForEach-Object { $_.Trim() } | Where-Object { $_ -and -not $_.StartsWith('#') } |
        ForEach-Object { if ($_.StartsWith('runs/')) { Join-Path $root $_ } else { $_ } })
    $cs = Join-Path $Out $dir.Name 'cs'
    $py = Join-Path $Out $dir.Name 'py'
    dotnet $dll.FullName analyze @argv --out $cs | Out-Null
    if ($LASTEXITCODE -ne 0) { $failed += "$($dir.Name): the C# analyze failed"; continue }
    & $Python -m oventracker analyze @argv --out $py | Out-Null
    if ($LASTEXITCODE -ne 0) { $failed += "$($dir.Name): the Python analyze failed"; continue }
    foreach ($pair in @(@($cs, $py, 'C# and Python differ'), @((Join-Path $dir 'expected'), $cs, 'C# differs from expected'))) {
        $diff = & $Python $compare $pair[0] $pair[1]
        if ($LASTEXITCODE -ne 0) { $failed += "$($dir.Name): $($pair[2])`n$($diff -join "`n")" }
    }
    Write-Host "$($dir.Name): checked"
}
if ($failed) { $failed | ForEach-Object { Write-Host $_ }; throw "$($failed.Count) difference(s)" }
Write-Host 'C# and Python agree on every case.'
