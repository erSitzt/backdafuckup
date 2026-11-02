# Plan: File-level restore (FLR)

Spec folder: `agent-os/specs/2026-11-02-1713-file-level-restore/`

## Context

The user calls file-level restore, SQL restore and AD restore **knock-out criteria** that come before notifications. The order is agent → **FLR** → SQL → AD. The Windows guest agent is in place (application-consistent backups confirmed on app01). Today a single file can only be recovered by restoring the whole VM.

The roadmap still lists FLR in phase 2; this spec moves it into the MVP.

**Already decided:**
- **Targets:** browser download, back into the VM, and to a network share.
- **File systems:** NTFS (GPT/MBR) and ext4/XFS, including LVM.

**User decisions (2026-11-02):**
- **UI:** no visuals; build it Veeam-like. Volumes/folders as a tree on the left, a file list on the right, multi-select, and actions on top.
- **Linux VMs "back into the VM":** **SSH/SFTP with stored credentials and sudo support**. A root login works directly.
- **Windows VMs "back into the VM":** through the existing guest agent. **NTFS permissions are restored, as an option that is on by default.**

## Parser library and ZIP (research result)

**LTRData/DiscUtils** (MIT, the maintained fork):
- NuGet 1.0.90 (Oct 2026), net10 + netstandard2.0, packages `.Core` (MBR/GPT/LDM), `.Streams`, `.Ntfs`, `.Ext`, `.Xfs`, `.Lvm`, `.Registry`.
- Pure managed code, so no root, kernel mount or FUSE is needed. It is synchronous and **not thread-safe** per file system instance.

**Gaps that must be closed:**
- **XFS:**
  - `nrext64` is not implemented (default in xfsprogs ≥ 6.6: Ubuntu 24.04+, RHEL 10), so files and folders read **silently as empty**.
  - `bigtime` timestamps are wrong.
  - Unwritten extents return old blocks.
  - Unknown features are not rejected.
- **ext4:** unwritten extents get the wrong length. Incompatible features (inline_data, meta_bg) are refused cleanly.
- **LVM:** linear only.
- **NTFS:** good (ADS, ACLs, sparse, LZNT1, WOF/CompactOS). Dedup stubs, EFS and cloud placeholders come back as zeros or ciphertext.

**Decision:**
- NuGet for `.Core`, `.Streams`, `.Ntfs`, `.Lvm` and `.Registry`.
- **The ext and XFS providers vendored as source** into `src/ThirdParty/DiscUtils.Ext` / `.Xfs` (MIT license + NOTICE). This is possible because providers have been registered explicitly since 2026-09.
  - Our patches: nrext64, bigtime, unwritten extents (both), rejecting unknown XFS incompat features.
  - Patches go upstream as PRs; we switch back to NuGet once they are released.
- **LVM v1: linear LVs** (the installer default); striped and thin later.
- **Rule: anything unknown fails loudly with a reason, never silently zeros.**

**ZIP:**
- `ZipArchive` (Create mode) streams with ZIP64, but in .NET 10 it writes **synchronously**, and Kestrel forbids that.
- So the ZIP is built on a dedicated thread into a `System.IO.Pipelines.Pipe`, which is copied asynchronously to the response.
- Tests: > 4 GiB entry and > 65,535 entries with unzip, 7-Zip, Windows Explorer and macOS. Fallback: our own store-only ZIP64 writer (sizes are known up front).

## What exists (reuse) — and corrections from the review

- **Disk reading:**
  - `RestorePointDiskImage.ReadAsync` (`src/Backdafuckup.DataMover/RestorePointDiskImage.cs`) gives random access but has no cache.
  - `IChunkReader` is **async-only**, so DiscUtils on top of it would be sync-over-async and starve the thread pool. That would delay agent long polls and freeze timing.
  - → Add a **synchronous chunk read** (`RandomAccess.Read` + `ChunkCodec.Decode`) to `LocalDirectoryRepository.Io.cs`.
  - The chunk size is per repository (64 KiB–16 MiB), so caches are limited in **bytes**.
- **Repository and pins:**
  - `RepositoryManager.GetAsync` (`Server/Engine/EngineInfrastructure.cs:110`); `IBackupRepository.Pin`.
  - `Pin` does not take the GC mutex → **after pinning, check again** that the restore point is Available.
  - Removing a repository closes it despite pins (`InfrastructureEndpoints.cs:204`) → **refuse while mounts are open**.
  - Retention retries pinned points only at the next job run (`BackupSessionRunner.cs:130`). That is acceptable; document it.
- **Sessions:**
  - `SessionRunnerService.HasCapacity` / `RunAsync` (`EngineServices.cs:88-112`) fail unknown kinds. Stop works only for `RunningSessions`, and StartupRecovery marks open sessions Failed.
  - Enums are stored as strings, so **new enum values need no migration**.
- **Agent:**
  - Transfer pattern: the self-update `GET api/agent/files/...` with `X-Agent-Key`, `AgentRegistry.Reserve`.
  - **Bug:** an unknown command goes to `Fail`, which **aborts a running VSS operation** (`CommandExecutor.cs:115,156`) → **capability gating** plus a fix.
  - net48 has no `DefaultConnectionLimit` (2 per host), and a new `HttpClient` must use `_handlerFactory`, or the certificate pin is lost (`AgentRunner.cs:36`).
- **SMB:** `Backdafuckup.Smb` (connect, create directories, overwrite). SMBLibrary can create-new (`FILE_CREATE`), set times (`FileBasicInformation`) and replace (`FileRenameInformationType2`). It is synchronous and not thread-safe.
- **Credentials:** `Credential` has a single `ProtectedSecret`, so Linux stores a protected JSON (password | key + passphrase, optional sudo password). `/api/credentials` and `Credentials.razor` handle Windows only so far.
- **Tests:**
  - `DataMoverRoundTripTests.BackupAsync` is private → move it to `Backdafuckup.Testing`.
  - `InMemoryDisk(byte[])` → plus a file-backed test disk (XFS needs ≥ 300 MiB, sparse).
  - `FakeGuestAgent` bypasses HTTP → transfer tests use the real `AgentRunner` against the test server (`ApiIntegrationTests.cs:213`).

## Architecture

### 1. Reading — new project `src/Backdafuckup.FileLevel`
- **`ChunkCache`** (singleton): global, keyed by `ChunkId` (content-addressed, so it is shared by all mounts and sessions), limited in bytes (default 512 MiB, `FileRestore:CacheBytes`), single-flight per chunk. Decoders are capped globally.
- **`RestorePointDiskStream : Stream`**: synchronous, seekable, read-only over the index + sync chunk read + cache. Zero chunks read as zeros; reads at the end are clamped. Read-ahead only if measurements show it is needed.
- **Volume discovery over all disks of the restore point:**
  - MBR (incl. extended)/GPT;
  - LVM linear (VG across several disks);
  - NTFS / ext2-4 / XFS.
  - **"Not readable" with a reason:** BitLocker, LUKS, Storage Spaces, ReFS, Btrfs, striped/thin LVs, unknown ext4/XFS features, LDM (unless the spike confirms simple volumes).
- **Stable volume ids:** manifest disk key + partition start LBA, or VG UUID + LV UUID.
- **`IGuestFileSystem`** adapter (list paged, stat, open, security descriptor / mode+uid+gid, link target) over DiscUtils:
  - It walks iteratively with caps (depth, entry count, time) and does not follow junctions/symlinks. **The guest image is untrusted input:** a stack overflow would end the server.
  - Files that cannot be restored (dedup, EFS, cloud placeholder) show a reason and fail per file.
- **Drive letters / mount points:**
  - **Windows:** `SYSTEM\MountedDevices` from the backup (DiscUtils.Registry): MBR signature + offset, or the GPT partition GUID. This also works for restore points without an agent.
  - **Linux:** `/etc/fstab` of the root FS (UUID/LABEL/device).

### 2. Browsing and downloading (server)
- **`FileRestoreMounts`** (singleton):
  - **One mount per user and restore point (reused)**: the pin, the streams, and **one locked** DiscUtils instance for browsing.
  - **Every download or restore opens its own instance** on its own stream.
  - A lease count, so the idle timer (30 min, `FileRestore:IdleTimeout`) never closes a mount mid-download. At most 4 mounts.
- **Session kinds:**
  - **`FileBrowse`**: not queued and not counted; created as Running. Its log is the **audit trail**: opened by, volumes, each download (files, bytes), each restore started (with a link), closed by user / idle / server restart.
  - **`FileRestore`**: queued, with its own limit (2).
  - `HasCapacity`, `RunAsync`, Stop and StartupRecovery get both kinds; a FileBrowse session ends as Stopped with "server restarted".
- **API** `/api/file-restore` (`Api/FileRestoreEndpoints.cs`, DTOs in `Contracts/FileRestore.cs`). All file system work runs on worker threads, never on request threads.
  - `POST mounts {restorePointId}` → mount + volumes. `GET mounts`, `DELETE mounts/{id}`.
  - `GET mounts/{id}/entries?volume=&path=&offset=&limit=` (paged with a total).
  - `GET mounts/{id}/file?volume=&path=`: single file with range support.
  - `POST mounts/{id}/downloads {volume, paths[]}` → token, then `GET downloads/{token}` → ZIP stream.
    - **Token:** 256-bit random, bound to the user and the mount, valid 60 s, consumed atomically, dead once the mount closes.
    - **ZIP:** names sanitized (`\` and `..`); the number of entries is capped.

### 3. Restore sessions — `FileRestoreSessionRunner`
- **Request:** `POST /api/file-restore/restores {restorePointId, volume, paths[], target, mode: Overwrite|Keep, restorePermissions}`. The runner opens its own instance and pin.
- **Allowed targets by source:** NTFS → Windows VM (agent), share, download. ext4/XFS → Linux VM (SSH), share, download.
- **Shared pipeline:**
  - enumerate the selection;
  - stream each file;
  - progress by bytes;
  - **per-file errors are collected → warning, no abort**;
  - **Keep** writes to `RESTORED-<name>`;
  - directory times (and ACLs) are set after the children;
  - symlinks/junctions are not followed. **Reparse points on Windows are skipped and logged in v1.** Linux symlinks are created last.
  - Before each write, check that the parent directories are real directories (no write through a link).
- **Windows VM (agent):**
  - The target is the original VM or another VM with an online agent. Paths: original location (drive letter) or an alternate folder.
  - **ACLs (owner + DACL):** on by default for the original location. For another VM or an alternate folder the default is off (local SIDs and inheritance do not fit there); it can be switched on.
  - **Protocol:**
    - `AgentHostInfo.Capabilities` (e.g. `restore-files-v1`). Only agents that report it get `RestoreFiles`; older agents are asked to update first.
    - Fix in `CommandExecutor`: unknown commands no longer abort a running backup.
    - `RestoreFiles {TransferId}` is acknowledged at once and runs in `AgentRunner` (async).
    - `GET api/agent/transfers/{id}/items?offset=` (paged; security descriptors once per `$Secure` id) and `GET …/items/{n}/content`.
    - `POST …/progress` (heartbeat + per-file results).
    - The server checks the agent key **and** the transfer's agent, state and item index, and caches authentication.
    - The transfer fails after N minutes without a heartbeat.
    - `AgentRegistry.Reserve` during the transfer.
  - **Agent writer** (`Agent.Core`, tested on Linux; ACL/privileges in the Windows project):
    - `\\?\` paths after normalizing (also net48 / 2012 R2) and a short random temp name, then replace.
    - Clear ReadOnly before replacing. A locked file is a per-file error.
    - SHA-256 of the written file, compared on the server.
    - Path guard: local fixed volumes only (it catches bugs; it is not a security boundary, because the server can push agent updates anyway).
    - `SeRestorePrivilege`/`SeTakeOwnershipPrivilege` for the owner.
    - net48: `ServicePointManager.DefaultConnectionLimit` raised. A transfer `HttpClient` from `_handlerFactory` (keeps the pin), without a total timeout, `ResponseHeadersRead`.
    - ADS and sparse flags are not restored in v1 (logged); hard links become copies (documented).
- **Linux VM (SSH.NET 2026.0.0, MIT):**
  - `CredentialKind.Linux`, plus `ssh_host_keys` (migration): **trust on first use**, and a mismatch fails with both fingerprints.
  - Address from the guest metrics or entered by hand; logged as `vm (ip):22`.
  - **One code path for root and sudo:**
    1. SFTP to `/var/tmp/bdfu-<random>/`. Data files are named only by number, and the manifest has target paths **base64-encoded**.
    2. **One** static script with sudo (`sudo -S -p ''`, retry with a PTY on `requiretty`, exit code via a marker): `install`/`mv` within the target file system, chown/chmod/touch, `restorecon` if present, Keep renaming, cleanup.
    3. **No file name ever ends up in a shell string.**
- **Share (SMB):**
  - `\\server\share\path` + a Windows credential; DNS resolution, logged as `server (ip):445`.
  - Create-new for Keep, timestamps, replace via rename, on a dedicated thread.
  - Linux names with `:*?"<>|\` are per-file errors.
  - No ACLs on shares.

### 4. Console (`src/Backdafuckup.Web`)
- `Backups.razor`: "Restore files" per restore point → `/files/{restorePointId}`.
- **`FileBrowser.razor`:**
  - A header with the VM, time and consistency (crash-consistent: a hint that the newest changes may be missing).
  - Left, `MudTreeView` with volumes/folders (lazy, folders only, capped). Right, `MudDataGrid` (virtualized, paged) with multi-select and a breadcrumb.
  - Actions: *Download*, *Restore → Overwrite / Keep*, *Copy to …* (dialog by source type).
  - Unreadable volumes and files with their reason. Unmounts on leaving the page.
- `Credentials.razor` + `/api/credentials`: Linux credentials (password or key, optional sudo password).

## Tasks (vertical slices; the suite stays green after each)

1. **Save the spec documentation** (`plan.md`, `shape.md` with all decisions, `standards.md`, `references.md`, `visuals/` empty).
2. **Spike (go/no-go per file system):**
   - fixture script `tests/fixtures/make-fs-images.sh` (Docker; `--privileged` only for loop/LVM/ntfs-3g);
   - **small sparse images**, committed zstd-compressed:
     - NTFS GPT and MBR;
     - ext4 with mke2fs 1.47 defaults (fallocated files, htree directory);
     - XFS with current defaults (nrext64, bigtime) and all directory formats;
     - LVM with a VG over two disks;
     - optionally an NTFS image from Windows (`make-ntfs-fixture.ps1`, run once by the user).
   - **Read-only:** browse the real restore point of app01 in the dev repository.
3. **Reading layer:** sync chunk read, `ChunkCache`, `RestorePointDiskStream`, test helpers to `Backdafuckup.Testing`.
4. **NTFS + partitions + `IGuestFileSystem`** (MBR/GPT, MountedDevices drive letters, dedup/EFS reasons, caps against hostile images).
5. **Slice 1:** `FileBrowse` mounts (leases, recovery, audit), paged entries, single-file download, a minimal `FileBrowser.razor` + button.
6. **ZIP download** with the token (Pipe, sanitizing, compatibility tests).
7. **Slice 2:** vendored ext/XFS providers with the patches, LVM linear, fstab mapping. **Linux browse + download.**
8. **`FileRestore` runner + shared pipeline + SMB target** (tests against the Samba container, only from inside its network namespace).
9. **Agent target:** capabilities and the executor fix, transfer endpoints, writer + ACLs, net48 connection limit; tests with the real `AgentRunner` over HTTP.
10. **SSH target:** Linux credentials (UI/API), host keys (migration), staging script with sudo/PTY. Tests against an `openssh-server` container (root, sudo with password, NOPASSWD, a hostile file name).
11. **Docs:**
    - Admin guide "File-level restore": ports 445/22/9555, sudo/requiretty, host keys, what is not supported, crash-consistent hint, idle timeout, retention while mounted.
    - Roadmap: FLR into the MVP.
    - THIRD-PARTY-NOTICES (DiscUtils, SSH.NET).
    - Lab runbook.
    - Recursive search later (follow-up).

12. **Lab provisioning with OpenTofu + Xen Orchestra** (user decision 2026-11-02):
    - provider `vatesfr/xenorchestra` (MPL-2.0) against the existing XO, in `deploy/lab/`;
    - test VMs in the existing pool, but only on their own SR/network and with the tag `bdfu-lab`:
      - Windows Server 2022/2025 (+ SQL Express);
      - Ubuntu 24.04 (ext4, XFS);
      - a RHEL-compatible 9/10 with LVM + XFS;
    - `tofu destroy` only for these tagged VMs from its own state, and only in a run the user explicitly starts;
    - needs the XO URL, an API token, the templates, the SR and the network from the user.

## Verification

- **Automated (Linux):**
  - Tests per task.
  - An engine round trip: fixture image → `FakeXenPool` VM → backup → mount → browse → file/ZIP download → SMB / SSH / agent target, comparing hashes.
  - Hostile images (cycles, deep nesting) do not end the process.
  - The full suite stays green; lab tests stay opt-in (`BDFU_LAB_RUN=1`).
- **Real data (read-only):** in the local instance, browse the restore point of app01, download a few files and compare their hashes with the files in the VM.
- **Lab (the user's test lab):**
  - Windows VM: Overwrite and Keep, with ACLs checked by `icacls`;
  - Linux VM as root and as a sudo user (owner/mode/SELinux);
  - share;
  - a ZIP > 4 GiB in Explorer and 7-Zip.

## Risks

1. **Parser coverage:**
   - Further mkfs defaults may bring new gaps. Mitigated by fixtures from current tools, clear "not supported" reasons, and the replaceable `IGuestFileSystem`.
   - The fork has one maintainer; our vendored providers reduce that dependency.
2. **Crash-consistent images:** journals (`$LogFile`, jbd2, XFS log) are not replayed, so the newest metadata may be missing. Shown as a hint in the UI.
3. **Agent and sudo as file writers:** the server can write files in the VM as SYSTEM/root. This is inherent (like Veeam) and documented. Mitigated by a path guard, no write through links, and no shell interpolation.
4. **sudo variants** (requiretty, targetpw, secure_path): PTY fallback, error texts that name host, user and step.
5. **Memory and threads:** a global cache in bytes, worker threads instead of pool threads, and limits on mounts and sessions.
