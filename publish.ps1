# Publishes Portabilizer as a self-contained, single-file x64 executable.
# Usage:  .\publish.ps1            -> Portabilizer.exe (and dist\Portabilizer.exe)
#         .\publish.ps1 -Run       -> publish and launch
# Refuses a version that's already in Versions\Archive: raise <Version> in the csproj first.
param([switch]$Run)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$proj = Join-Path $root "src\Portabilizer\Portabilizer.csproj"
$out  = Join-Path $root "dist"
$version = ([xml](Get-Content $proj)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
$archive = Join-Path $root "Versions\Archive\Portabilizer-$version.exe"
if (Test-Path $archive) { throw "Version $version is already archived. Raise <Version> in the csproj, then publish again." }

Get-Process Portabilizer -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$root*" } | Stop-Process -Force

Write-Host "Publishing Portabilizer (Release, win-x64, self-contained, single file)..." -ForegroundColor Cyan
dotnet publish $proj -c Release -o $out
if ($LASTEXITCODE -ne 0) { throw "publish failed" }

# Only the exe is needed; drop symbol/side files so the folder stays clean.
Get-ChildItem $out -File | Where-Object { $_.Name -ne "Portabilizer.exe" } | Remove-Item -Force

$exe = Join-Path $out "Portabilizer.exe"
# Every build is kept: the current one at the top level, and a copy per version in Versions\Archive.
New-Item -ItemType Directory -Force (Split-Path $archive) | Out-Null
Copy-Item $exe $archive
Copy-Item $exe (Join-Path $root "Portabilizer.exe")
$size = [math]::Round((Get-Item $exe).Length / 1MB, 1)
Write-Host "Done: $exe ($size MB)" -ForegroundColor Green
if ($Run) { Start-Process $exe }
