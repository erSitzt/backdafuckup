# Installs backdafuckup as a Windows service. Run as Administrator from the folder with the published files.
# Configuration: %ProgramData%\backdafuckup\appsettings.json (see deploy/linux/appsettings.example.json).
# -InstantRecoveryFirewall opens the ports of the NFS server for instant VM recovery (portmapper 111 TCP/UDP, NFS 2049 TCP,
# MOUNT 20048 TCP/UDP), for the addresses in -NfsClients (the ESXi hosts' VMkernel addresses) or for any address without
# it. The NFS server itself is off until InstantRecovery:Enabled is set in %ProgramData%\backdafuckup\appsettings.json.
param(
    [string]$InstallDir = "$env:ProgramFiles\backdafuckup",
    [string]$ServiceName = "backdafuckup",
    [switch]$InstantRecoveryFirewall,
    [string[]]$NfsClients = @()
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
if ($InstantRecoveryFirewall) {
    $remote = if ($NfsClients.Count -gt 0) { $NfsClients } else { @("Any") }
    $rules = @(
        @{ Name = "backdafuckup NFS portmapper (TCP)"; Protocol = "TCP"; Port = 111 },
        @{ Name = "backdafuckup NFS portmapper (UDP)"; Protocol = "UDP"; Port = 111 },
        @{ Name = "backdafuckup NFS (TCP)"; Protocol = "TCP"; Port = 2049 },
        @{ Name = "backdafuckup NFS mount (TCP)"; Protocol = "TCP"; Port = 20048 },
        @{ Name = "backdafuckup NFS mount (UDP)"; Protocol = "UDP"; Port = 20048 }
    )
    foreach ($rule in $rules) {
        Get-NetFirewallRule -DisplayName $rule.Name -ErrorAction SilentlyContinue | Remove-NetFirewallRule
        New-NetFirewallRule -DisplayName $rule.Name -Direction Inbound -Protocol $rule.Protocol -LocalPort $rule.Port `
            -RemoteAddress $remote -Program $exe -Action Allow | Out-Null
    }
    Write-Host "Firewall rules for instant recovery's NFS server added (from: $($remote -join ', '))."
}

Start-Service -Name $ServiceName
Write-Host "Service '$ServiceName' installed and started. Web console: https://localhost:9555"
