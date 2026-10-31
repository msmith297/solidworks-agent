<#
.SYNOPSIS
  Builds a Release add-in and the Windows installer locally (needs Inno Setup 6: https://jrsoftware.org/isinfo.php).
  Releases are normally built by GitHub Actions (.github/workflows/release.yml) when a v* tag is pushed.
.EXAMPLE
  .\scripts\make-installer.ps1 -Version 0.2.0      # -> dist\SolidWorks-Agent-Setup-0.2.0.exe (+ .sha256)
#>
param([Parameter(Mandatory = $true)][ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version)
$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent

& (Join-Path $PSScriptRoot "build.ps1") -Configuration Release -Version $Version

$redist = Join-Path $root "installer\redist"
New-Item -ItemType Directory -Force $redist | Out-Null
$webview = Join-Path $redist "MicrosoftEdgeWebview2Setup.exe"
if (-not (Test-Path $webview)) { Invoke-WebRequest "https://go.microsoft.com/fwlink/p/?LinkId=2124703" -OutFile $webview }

$iscc = @("${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe", "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe") |
    Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw "Inno Setup 6 isn't installed (https://jrsoftware.org/isinfo.php)." }
& $iscc "/DAppVersion=$Version" (Join-Path $root "installer\SolidWorksAgent.iss")
if ($LASTEXITCODE) { throw "Inno Setup failed" }

Get-ChildItem (Join-Path $root "dist") -Filter "*-$Version.exe" | ForEach-Object {
    "$((Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLower())  $($_.Name)" | Set-Content -NoNewline -Encoding ascii "$($_.FullName).sha256"
    Write-Host "Installer: $($_.FullName)" -ForegroundColor Green
}
