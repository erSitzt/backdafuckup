# Plan: Active Directory backup and object-level restore

Spec folder: `agent-os/specs/2026-11-06-2308-ad-object-restore/`

## Context

Restoring Active Directory objects is the next knock-out criterion after SQL Server (order agent → FLR → SQL → AD). The roadmap lists it in phase 1: "application-aware AD backup with object-level restore".

**Today nothing is DC-aware:**
- No domain controller is detected.
- The NTDS writer's file list is dropped in `NativeVss.ToWriter` (`src/Backdafuckup.Agent/NativeVss.cs:224`).
- Every VSS backup is copy-only (`AgentQuiescer.cs:272`), so AD never records a backup: event 2089 appears and the backup age is unknown.
- There is no AD browse or restore.
- A DC restored as a VM is not protected by a fresh VM generation id, which risks USN rollback.

**User decisions (2026-11-06):**
- **Scope v1:**
  - deleted objects come back, both with and without the AD Recycle Bin;
  - objects without a tombstone are re-created with a new SID and GUID, with a warning;
  - changed attributes are put back;
  - GPOs are restored (GPC in AD plus GPT in SYSVOL).
- **Passwords:** no hashes from the backup. When the password is gone (tombstone or re-created object), the admin sets a new one, or the account stays disabled with "must change at next logon".
- **Reading the backup:** by Microsoft's documented AD Database Mounting Tool (`dsamain.exe`) on a live DC of the domain, through our agent, and LDAP for everything. This fits the policy "publicly documented APIs only" (`agent-os/product/tech-stack.md`). Veeam and Quest read ntds.dit with their own, undocumented readers.
- **DC backups are VSS full backups,** so AD records them; copy-only stays for all other VMs.
- **Lab:** a test forest built with OpenTofu. There are no DCs yet. Nothing is ever written to `corp.example`.
- **Visuals:** none.
- **References:** the SQL pieces (SQL browser, SQL session runners, SQL job framework).

**How other products do it:** Veeam reanimates tombstones and Recycle Bin objects over LDAP and re-creates objects that no longer have a tombstone, with a new SID. It restores passwords from the backup's hive. Its GPO restore covers GPC plus GPT. Quest reanimates tombstones and re-applies the attributes over ADSI; passwords need its agent.

## Approach

### 1. DC awareness in the backup

**Agent:**
- `AgentHostInfo` gains the domain role (`Win32_ComputerSystem.DomainRole` 4/5), the domain DNS and NetBIOS names, and the forest.
- At prepare on a DC:
  - keep the NTDS writer's files (ntds.dit, log folder);
  - read rootDSE over LDAP to localhost: naming contexts, `dsServiceName`, invocationId, `highestCommittedUSN`, domain and forest functional levels;
  - read whether the Recycle Bin is enabled (`msDS-EnabledFeature`);
  - read `tombstoneLifetime` and `msDS-DeletedObjectLifetime`, the DC's OS version, and the SYSVOL path (`Netlogon\Parameters\SysVol`).
- New job/protocol fields go behind capability `ad-backup-v1`.

**VSS full for DCs:** the server sends `CopyOnly = false` when the agent reports a DC (`AgentQuiescer.SendAsync`). If the DC also runs SQL Server with our log backups, copy-only stays and the session warns, because a VSS full would be a foreign full in the SQL chain.

**Restore point:**
- `guest-metadata` v3 adds an `ActiveDirectory` section (`StoredAdDatabase`):
  - domain, DC, invocationId, USN, levels;
  - Recycle Bin on/off, tombstone and deleted-object lifetimes;
  - dit, log and SYSVOL paths;
  - the NTDS writer state, giving consistency and its reason.
- Pattern: `StoredGuestMetadata`/`StoredSqlDatabase` (`src/Backdafuckup.Server/Sql/StoredGuestMetadata.cs`).
- The catalog gets `restore_points.ad_domain` (nullable). Older points are backfilled from the SYSTEM hive: `Select\Current` → `Services\NTDS\Parameters`, `Netlogon\Parameters\SysVol`, `Tcpip\Parameters\Domain`. Pattern: `SqlCatalog.ReadInstances` and `SqlCatalogBackfill`.

**Console:** an "AD" chip on restore points, and the Restore menu entry "Active Directory objects" (`/ad/{restorePointId}`). Pattern: `Backups.razor` SQL entry.

### 2. Safe DC VM restore

- **Spike:** XAPI `generation_id` on `import_metadata` with `restore=true` and with `restore=false` (`XenRestoreSession.CreateVmAsync`, `src/Backdafuckup.Xen/XenRestoreSession.cs:163`).
- **Generation id:** a restored DC VM always gets a new VM generation id. Windows Server 2012+ then restores itself non-authoritatively: new invocationId, RID pool dropped.
- **Wizard warnings:** a domain controller is in the restore point; the original DC still runs; an authoritative restore is described in the docs.

### 3. AD explore session (browse, compare)

**Opening:**
1. The admin picks the restore point, a **target DC agent** (online, same domain, capability `ad-restore-v1`) and a Windows account. The account needs to reanimate tombstones, write the objects and write SYSVOL; a domain admin works, delegated rights are documented.
2. **Server:**
   - opens the restore point with the file-level reader (`FileRestoreMounts.OpenDisksAsync` + `GuestImage` + `IGuestFileSystem.OpenRead`);
   - plans ntds.dit, `edb.chk` and `edb*.log` (paths from the metadata or the SYSTEM hive);
   - transfers them with the existing restore-files transfer (`AgentFilePlan`, SHA-256 checked) into `%ProgramData%\backdafuckup\ad\<session>` on the DC. That folder is created by the agent with an ACL for SYSTEM and Administrators only. The free space is checked first.
3. **Agent job `ad-mount`:**
   - soft recovery with `esentutl /r` if the spike shows it is needed;
   - then `dsamain.exe /dbpath … /ldapport <free port>` as a child process, with the process id in a journal;
   - waits until an LDAP bind to `localhost:<port>` works.

**Queries:** short, read-only agent jobs outside the work slot. Each runs LDAP against the backup (dsamain) and against the live AD (localhost:389) as the account, with the Show-Deleted control:
- `ad-tree`: OUs and containers;
- `ad-search`: by name, sAMAccountName or class, filter "deleted since the backup / changed / gone", paged;
- `ad-object`: all attributes in the backup vs. live, matched by objectGUID; the state is unchanged / changed / in Deleted Objects (Recycle Bin or tombstone) / gone (recycled or never there); memberships in both;
- `ad-gpos`: GPCs with versions, links and the SYSVOL folder state.

**Closing:**
- `ad-unmount` stops dsamain and deletes the folder, which holds the password hashes.
- The same happens at an idle timeout (1 h), when the session ends, and at agent start from the journal.

### 4. Object restore (session kind `AdRestore`)

The agent job `ad-restore` works per selected object, parents first. Each step writes a log line naming the DN, the attributes, and old → new values; secrets are never logged. Nothing in live AD is deleted, only reanimated, added or modified. The steps by case:

**In Deleted Objects:** reanimate over LDAP (delete `isDeleted`, set `distinguishedName` under the original or a chosen parent). The documented "Reanimate Tombstones" right is required. After it:
- **with the Recycle Bin:** done; attributes and links come back by themselves;
- **tombstone:**
  - write the backup's writable attributes back; system-only, constructed, operational and back-link attributes are skipped, and so are objectGUID, objectSid and passwords;
  - add the memberships back into the groups (back-link `memberOf` from the backup → the groups' `member`);
  - set the password, or leave the account disabled with `pwdLastSet = 0`.

**Gone (recycled or past the lifetime):** re-create it with the backup's attributes and memberships under its parent. The account gets a new SID and GUID; the dialog and the log say what does not follow (ACLs, references in other domains).

**Changed (object exists):** write back the chosen attributes:
- linked attributes are mapped to live DNs by GUID;
- large multi-valued sets get an add/remove diff instead of a replace;
- an object that was moved goes back to the backup's parent (ModifyDN), as an option.

**Passwords:** `unicodePwd` over an LDAP connection with sealing (Negotiate, port 389), or LDAPS. The value is never logged.

### 5. GPO restore

- **GPC:** reanimated or re-created with the same `{GUID}` name, including its child containers `User` and `Machine`. Restored attributes: `displayName`, `gPCFileSysPath` (adjusted to the live SYSVOL), `gPCMachine/UserExtensionNames`, `flags`, `gPCWQLFilter`, `nTSecurityDescriptor` (DACL, with the SD-flags control).
- **GPT:**
  1. The backup's `SYSVOL\domain\Policies\{GUID}` is read from the restore point and transferred into a staging folder on the DC.
  2. The current folder is moved aside as `{GUID}.bdfu-replaced`.
  3. The staging folder is swapped in, with the backup's NTFS ACLs (`IGuestFileSystem.GetSecurityDescriptor`).
  4. The folder set aside is deleted when the restore is done.
- **Version:** the user and machine halves are each set to max(live, backup) + 1, in both GPT.INI and `versionNumber`. Clients then apply the GPO again and DFS-R replicates it.
- **Links:** `gPLink` on the domain, OUs and sites, as an option (an attribute restore of those objects).
- **WMI filters:** later.

### 6. Console

- **Page `/ad/{restorePointId}`:**
  1. Start panel: target DC, account, Connect.
  2. On the left the OU tree; above it search and filters (deleted / changed / gone / all).
  3. Object list with state chips.
  4. Object view: the attribute table backup vs. live, with the differences highlighted and checkboxes, plus memberships.
  5. GPO tab: versions, links, differences.
- **Restore dialog:**
  - a preview of every step (reanimate / re-create / revert attributes / GPO);
  - options: password, enabled, memberships, links;
  - a confirmation that names the live domain and DC.
- Sessions page entries for AD explore and AD restore (`Fmt.Kind`).
- Patterns: `SqlBrowser.razor`, `SqlDatabase.razor`, `SqlTargetChecks`.

### Server and agent structure

- **Contracts:** `src/Backdafuckup.Contracts/Ad.cs`.
- **Server:**
  - `src/Backdafuckup.Server/Ad/`: `AdCatalog`, `AdExploreSessions` (like `FileRestoreMounts`, pins the restore point), `AdJobs` (like `SqlJobs`), `AdRestoreSessionRunner`;
  - endpoints `/api/ad/...`;
  - migration for `ad_domain`.
- **Agent.Core (netstandard2.0, testable on Linux):** `IAdEnvironment`, `AdJobRunner`, `AdWorkJournal`.
- **Agent:** `WindowsAdEnvironment` (dsamain/esentutl process control, LDAP via `System.DirectoryServices.Protocols` (MIT, Microsoft), SYSVOL file operations, ACLs). Accounts go through `WindowsLogon` and impersonation.
- **Tests:** `ScriptedAdEnvironment` (like `ScriptedSql`) for API tests with the real `AgentRunner`.

## Tasks

1. **Save the spec documentation:**
   - `plan.md`
   - `shape.md`: decisions, how others do it, report requirements
   - `standards.md`: earlier specs' conventions plus the AD additions — destructive writes need a preview and a confirmation; no live deletions; secrets never logged; the work folder is always deleted
   - `references.md`: SQL pieces, file-level reader, transfer
   - `visuals/`: none
2. **Lab forest via OpenTofu** (`deploy/lab`):
   - Windows VMs promoted with `Install-ADDSForest` to `bdfu-lab.local`, and a second DC;
   - test data: OUs, users, groups (nested), computers, GPOs with settings and links;
   - the Recycle Bin switchable.
   
   The user runs `tofu apply`; the agent is installed from the console.
3. **Spikes** (`spike.md`, on the lab DCs):
   - dsamain on a copied ntds.dit (recovery, port, access, Show-Deleted, newer dit on an older DC);
   - the NTDS writer with VSS full vs. copy (`repadmin /showbackup`);
   - XAPI `generation_id`;
   - LDAP reanimation with and without the Recycle Bin;
   - `unicodePwd` over sealing;
   - GPO version handling and DFS-R.
4. **DC awareness:**
   - agent role and facts;
   - NTDS files;
   - guest-metadata v3;
   - VSS full for DCs;
   - catalog field and backfill;
   - the console chip and menu entry.
   
   API tests with a fake DC agent.
5. **Safe DC VM restore:** the generation id and the wizard warnings. Tests on the XAPI fake.
6. **Agent AD layer:**
   - job kinds and capability;
   - dsamain lifecycle, journal and cleanup;
   - LDAP search, object and diff.
   
   Agent unit tests with a fake LDAP layer.
7. **Server explore sessions:**
   - file transfer to the DC, mount, queries, endpoints, idle timeout;
   - API tests with `ScriptedAdEnvironment`.
8. **Object restore:**
   - reanimate (with and without the Recycle Bin), attributes, memberships, re-create, passwords, ordering;
   - the `AdRestore` runner and the audit log;
   - tests.
9. **GPO restore:** GPC, GPT, versions, links. Tests.
10. **Console:** AD explorer page, restore dialog, session pages. Playwright check at 1400/1024/390.
11. **Docs:**
    - admin guide section "Active Directory" (rights, Recycle Bin, what comes back, limits, DC VM restore);
    - runbook section H;
    - roadmap;
    - `THIRD-PARTY-NOTICES` (System.DirectoryServices.Protocols).
12. **Lab checks H1–Hn with the user:**
    - deleted user, group and OU, with and without the Recycle Bin;
    - attributes, re-create, GPO, DC VM restore.

## Verification

- **Unit and API tests:**
  - DC detection and metadata v3, VSS full for DCs;
  - explore session (transfer, mount, queries, cleanup on close, timeout and agent restart);
  - restore cases against `ScriptedAdEnvironment`: reanimate, tombstone plus attributes, re-create, attribute revert, memberships, password never logged;
  - GPO restore (GPT swap, versions);
  - generation id on DC restore.
- **Full suite** green.
- **Lab** (the user runs tofu, the restores run through the console or the API):
  - runbook section H on the lab forest;
  - checks with `Get-ADObject -IncludeDeletedObjects`, `repadmin /showbackup`, `gpresult`, and SYSVOL on both DCs.

## Risks

1. **ntds.dit version:** dsamain cannot open a dit from a newer Windows (e.g. 2025 with 32k pages) on an older DC. The target DC must be at the same or a newer version; the dialog checks it.
2. **Writes into a live AD:** a preview, a confirmation, and adds/modifies only. Re-created objects have new SIDs, which the warning names.
3. **Large domains:** searches are paged; the server never holds the whole directory.
4. **ntds.dit on the DC holds hashes:** a protected folder, cleanup guaranteed by journal, the copy never leaves the DC.
5. **Slow storage:** VSS hold limit, so a crash-consistent dit. ESE recovery handles it; the spike checks dsamain with it.

## Not in this spec

Passwords or SID history from the backup, cross-domain restore, WMI filters, AD-integrated DNS zones and the configuration partition (sites, services), authoritative restore through the product, AD forest recovery.
