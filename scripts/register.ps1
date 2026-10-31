<#
.SYNOPSIS
  Registers the add-in with Windows (COM) and SolidWorks. Run from an elevated (Administrator) PowerShell.
  Restart SolidWorks afterwards; the add-in then appears under Tools > Add-Ins and loads at startup.
#>
param([ValidateSet("Debug", "Release")][string]$Configuration = "Debug")
$ErrorActionPreference = "Stop"
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) { throw "Run this script from an Administrator PowerShell (registering COM add-ins writes to HKLM)." }

$root = Split-Path $PSScriptRoot -Parent
$dll = Join-Path $root "src\SwAgent\bin\$Configuration\net48\SwAgent.dll"
if (-not (Test-Path $dll)) { throw "Not built yet: $dll. Run .\scripts\build.ps1 -Configuration $Configuration first." }

$guid = "{6D2B1C1E-4A0F-4F1E-9C7B-3E5A8D2F7B41}"

# Every registration leaves a per-version subkey under InprocServer32, and .NET loads the highest version listed -
# so an older build stamped with a higher version (e.g. an installed release, or a build made without -Version)
# would silently win. Clear them so the build registered now is the one SolidWorks loads.
$server = "HKLM:\SOFTWARE\Classes\CLSID\$guid\InprocServer32"
if (Test-Path $server) { Get-ChildItem $server | Remove-Item -Recurse -Force }

$regasm = Join-Path $env:windir "Microsoft.NET\Framework64\v4.0.30319\RegAsm.exe"
$ErrorActionPreference = "Continue" # RegAsm's "unsigned assembly with /codebase" warning goes to stderr; only the exit code matters
& $regasm $dll /codebase /nologo
if ($LASTEXITCODE) { throw "RegAsm failed ($LASTEXITCODE)" }
$ErrorActionPreference = "Stop"
if (Test-Path "HKLM:\SOFTWARE\SolidWorks\Addins\$guid") {
    Write-Host "Registered SolidWorks Agent ($guid). Restart SolidWorks to load it." -ForegroundColor Green
} else {
    Write-Warning "COM registration succeeded but the SolidWorks add-in key is missing. Check $env:LOCALAPPDATA\SwAgent\logs."
}
