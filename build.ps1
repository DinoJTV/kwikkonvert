<#
.SYNOPSIS
  Builds, tests and publishes KwikKonvert as a self-contained folder you can copy anywhere.

.EXAMPLE
  ./build.ps1                 # x64 Release -> .\publish\win-x64\KwikKonvert.exe
  ./build.ps1 -Platform ARM64 # Snapdragon / ARM PCs
  ./build.ps1 -Run            # build and start it
#>
param(
    [ValidateSet('x64', 'ARM64')] [string] $Platform = 'x64',
    [switch] $Run
)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

Write-Host "such tests..." -ForegroundColor Magenta
dotnet run --project tests/KwikKonvert.Core.Tests -c Release
if ($LASTEXITCODE -ne 0) { throw "Core tests failed." }

$rid = "win-$($Platform.ToLower())"
$out = Join-Path $PSScriptRoot "publish\$rid"
Write-Host "much build ($rid)..." -ForegroundColor Magenta
dotnet publish src/KwikKonvert/KwikKonvert.csproj -c Release -p:Platform=$Platform -r $rid --self-contained true -o $out
if ($LASTEXITCODE -ne 0) { throw "Publish failed." }

Write-Host "wow. $out\KwikKonvert.exe" -ForegroundColor Green
if ($Run) { Start-Process (Join-Path $out 'KwikKonvert.exe') }
