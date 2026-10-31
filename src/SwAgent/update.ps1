<#
  Installs a downloaded update of the add-in. Started by the add-in (Updater.LaunchInstaller) from a copy outside
  the install folder, because the installer replaces that folder.
  1. Waits for SolidWorks (WaitPid) to exit - the add-in DLLs are locked while it runs.
  2. Runs the installer silently (it asks for administrator rights: the add-in is registered machine-wide).
  3. Starts SolidWorks again if -Restart was given.
#>
param(
    [Parameter(Mandatory = $true)][string]$Installer,
    [Parameter(Mandatory = $true)][int]$WaitPid,
    [string]$Restart,
    [string]$Log = "$env:LOCALAPPDATA\SwAgent\logs\update.log"
)

function Write-Log([string]$message) {
    try { Add-Content -Path $Log -Value ("{0:yyyy-MM-dd HH:mm:ss} {1}" -f (Get-Date), $message) } catch { }
}

function Show-Message([string]$text) {
    Add-Type -AssemblyName System.Windows.Forms
    [System.Windows.Forms.MessageBox]::Show($text, "SolidWorks Agent update", "OK", "Warning") | Out-Null
}

Write-Log "Update helper started: installer=$Installer pid=$WaitPid restart=$Restart"

# 1. Wait for SolidWorks to close (it may take a while to save and shut down).
$deadline = (Get-Date).AddMinutes(10)
while ((Get-Process -Id $WaitPid -ErrorAction SilentlyContinue) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 500 }
if (Get-Process -Id $WaitPid -ErrorAction SilentlyContinue) {
    Write-Log "SolidWorks is still running after 10 minutes; update postponed."
    Show-Message "SolidWorks didn't close, so the SolidWorks Agent update wasn't installed. It will be offered again next time."
    exit 1
}
Start-Sleep -Seconds 2 # let Windows release the DLLs

# 2. Install.
try {
    $p = Start-Process -FilePath $Installer -ArgumentList "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/SP-" -Verb RunAs -Wait -PassThru
    Write-Log "Installer exited with code $($p.ExitCode)"
    if ($p.ExitCode -ne 0) { Show-Message "The SolidWorks Agent update didn't finish (installer code $($p.ExitCode)). Details: $Log" }
}
catch {
    # Most often: the administrator prompt was declined.
    Write-Log "Installer could not run: $($_.Exception.Message)"
    Show-Message "The SolidWorks Agent update wasn't installed: $($_.Exception.Message)"
}

# 3. Start SolidWorks again.
if ($Restart -and (Test-Path $Restart)) {
    Write-Log "Restarting SolidWorks: $Restart"
    Start-Process -FilePath $Restart
}
