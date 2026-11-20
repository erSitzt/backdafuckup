# References for the Windows Backup Proxy

## In this repository
- **Proxy agent:** `src/Backdafuckup.Proxy/ProxyAgentClient.cs` (commands, hello), `BlockDevices.cs` (`IBlockDevices`, `IOpenDevice`, `FileDevice`), `San.cs` (`IIscsiInitiator`, `CommandRunner`, portal probe), `ProxySelfUpdate.cs`, `src/Backdafuckup.ProxyAgent/Program.cs` (composition, enroll, update).
- **Server:** `src/Backdafuckup.Server/Proxies/ProxyAgents.cs` (update offer, attach, iSCSI login), `ProxyPackage.cs`, `ProxyInstallScript.cs`, `src/Backdafuckup.Server/Api/ProxyEndpoints.cs`, `src/Backdafuckup.Server/Arrays/ArraySnapshotService.cs` (proxy choice, teardown).
- **Windows service and self-update:** the guest agent, `src/Backdafuckup.Agent/Program.cs` (`ServiceBase`, exit code 3), `AgentInstaller.cs` (`sc.exe`, failure actions, folder ACL), `src/Backdafuckup.Agent.Core/AgentRunner.cs` (update by renaming the running file), `AgentFiles.cs` (log rotation).
- **Windows iSCSI and disks through PowerShell:** `src/Backdafuckup.Agent/WindowsPublishHost.cs` (`Get-InitiatorPort`, `Update-HostStorageCache`, `Set-Disk`, `-EncodedCommand` helper).
- **P/Invoke:** `src/Backdafuckup.Tape/Scsi/WindowsScsi.cs` (`CreateFileW`, `DeviceIoControl`, `IOCTL_STORAGE_QUERY_PROPERTY`, aligned buffers) with its Linux-testable layout class.
- **Certificate pinning on .NET Framework:** `src/Backdafuckup.Agent.Core/ServerCertificatePin.cs`.
- **Builds for two RIDs:** `scripts/build-tape-recover.sh`.

## Public documentation
- Microsoft Learn: `IOCTL_STORAGE_QUERY_PROPERTY`, `STORAGE_DEVICE_ID_DESCRIPTOR`, `IOCTL_DISK_SET_DISK_ATTRIBUTES`, `IOCTL_DISK_GET_LENGTH_INFO`, `FILE_FLAG_NO_BUFFERING` (alignment), `Set-StorageSetting -NewDiskPolicy`, `Get-IscsiSession`, `Update-HostStorageCache`.
- SPC-4: VPD page 0x83, designator type 3 (NAA).
- curl: `--pinnedpubkey` (TLS backends that support it).
- The other backup product's public help center, "Direct SAN Access": the SAN policy it sets on its proxies.
