# Installs backdafuckup as a Windows service. Run as Administrator from the folder with the published files.
# Configuration: %ProgramData%\backdafuckup\appsettings.json (see deploy/linux/appsettings.example.json).
param(
    [string]$InstallDir = "$env:ProgramFiles\backdafuckup",
    [string]$ServiceName = "backdafuckup"
)

$ErrorActionPreference = "Stop"
New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
Copy-Item -Path (Join-Path $PSScriptRoot "*") -Destination $InstallDir -Recurse -Force -Exclude "install-service.ps1"
New-Item -ItemType Directory -Force -Path "$env:ProgramData\backdafuckup" | Out-Null

$exe = Join-Path $InstallDir "backdafuckup.exe"
if (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) {
    Stop-Service -Name $ServiceName -Force
    sc.exe delete $ServiceName | Out-Null
    Start-Sleep -Seconds 2
}

sc.exe create $ServiceName binPath= "`"$exe`"" start= delayed-auto DisplayName= "backdafuckup backup server" | Out-Null
sc.exe description $ServiceName "Open-source VM backup for XCP-ng / XenServer" | Out-Null
sc.exe failure $ServiceName reset= 86400 actions= restart/10000/restart/60000/restart/300000 | Out-Null
Start-Service -Name $ServiceName
Write-Host "Service '$ServiceName' installed and started. Web console: https://localhost:9555"
