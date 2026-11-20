# Spike: Windows proxy (2026-11-20)

On the lab Windows VM (Windows Server 2022 Standard Evaluation, 10.0.20348, a VMware virtual disk on LSI Logic SAS), through VMware Tools guest operations. Read-only apart from a scratch folder.

| Question | Result |
|---|---|
| Does Windows' curl.exe pin the server key with `--insecure`? | **Yes.** curl 7.79.1 with Schannel: the right `--pinnedpubkey sha256//…` with `--insecure` answers (exit 0), a wrong one fails with exit 90 "public key does not match pinned public key". Without `--insecure` the self-signed certificate fails (exit 60). The one-liner can use curl.exe as on Linux. |
| Which property has the LUN's WWN? | `StorageDeviceIdProperty` (8) returned only the 12-byte header with one identifier and no data. **`StorageDeviceUniqueIdProperty` (3)** returns `STORAGE_DEVICE_UNIQUE_IDENTIFIER`; at its `StorageDeviceIdOffset` lies the `STORAGE_DEVICE_ID_DESCRIPTOR` with the page 0x83 identifier: code set 1 (binary), type 3 (NAA), association 0 (device), 16 bytes, the same value as `Get-Disk.UniqueId`. Windows builds its unique id from it. |
| Raw reads without the cache | `\\.\PhysicalDriveN` with `GENERIC_READ` and `FILE_FLAG_NO_BUFFERING`: a page-aligned 1 MiB read at 1 MiB works. An offset of 100 or a length of 1000 fail with error 87 (`ERROR_INVALID_PARAMETER`). A buffer off by one byte worked on this adapter, but other adapters have alignment masks. Unaligned requests need a sector-aligned bounce buffer. |
| Sector size | `StorageAccessAlignmentProperty` (6): logical 512, physical 512. |
| Rescan | `Update-HostStorageCache` takes about 7 s. Only once per snapshot: wait-device lists the disks without a rescan. |
| SAN policy | `Get-StorageSetting`: `OfflineShared` (the Windows Server default for shared buses). |
| iSCSI | MSiSCSI stopped (Manual) on this VM, no initiator port, no sessions: a Windows proxy without iSCSI reports that and offers no SAN access. |

Not tried in the spike: service install, update and the restart with exit code 3. The guest agent does exactly this, and the first install of the proxy (W1) tests it.
