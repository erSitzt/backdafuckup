# Spikes on the lab DCs (task 3)

The lab forest `bdfu-lab.local` (deploy/lab: `dc1` with `ad_forest`, later `dc2` with `ad_replica`). Nothing here touches `corp.example`. Each result goes into the table at the end, with the date.

## S1 — NTDS writer and VSS full

1. Job with dc1, guest processing VSS, no SQL log backups. Run a backup.
2. Session log: "dc1 is a domain controller of domain bdfu-lab.local; VSS full backup".
3. On dc1:
   - `repadmin /showbackup` → the "last backup" time of every partition is the backup's time;
   - event log "Directory Service": event 1917 (backup) and no further 2089 for these partitions.
4. If `repadmin` shows no backup: check whether the NTDS writer's component was added. `DescribeWriters` adds only selectable components; in the restore point, the guest metadata lists the writer's components. With a non-selectable top-level component, `DescribeWriters` must add it as well.

## S2 — dsamain on a copy of ntds.dit

1. From the restore point (file-level restore): copy `C:\Windows\NTDS\ntds.dit`, `edb.chk` and `edb*.log` into `C:\ProgramData\backdafuckup\ad\spike` on dc1.
2. `esentutl /mh ntds.dit` → state "Dirty Shutdown" or "Clean Shutdown"? If dirty: `esentutl /r edb /l . /s . /d .` (soft recovery in the copy's folder).
3. `dsamain /dbpath C:\ProgramData\backdafuckup\ad\spike\ntds.dit /ldapport 51389 /allownonadminaccess` (then also without `/allownonadminaccess`).
4. `ldp` or PowerShell `Get-ADUser -Server localhost:51389 -Filter *` → the users of the backup; with the Show-Deleted control: `Get-ADObject -Server localhost:51389 -IncludeDeletedObjects -Filter 'isDeleted -eq $true'`.
5. Bind as a domain admin and as a normal user: who may read what?
6. The free port: what happens if 51389 is taken?
7. Stop dsamain (Ctrl+C / process stop) and delete the folder: are any files still locked?
8. The agent's command lines (`WindowsAdEnvironment`): `esentutl /r edb /l <f> /s <f> /d <f> /o` on the copy, then `dsamain /dbpath <f>\ntds.dit /logpath <f> /ldapport p /sslport p+1 /gcport p+2 /gcsslport p+3 /allowUpgrade`. Are `/logpath` and `/allowUpgrade` accepted, is the recovery needed at all, and does dsamain open a database from a VSS snapshot without it?
9. dsamain listens on all interfaces: is the port reachable from another machine (Windows Firewall on the DC)? If yes, a firewall rule for the session or a note in the docs.
10. Kerberos vs. NTLM: the agent connects to `<dc fqdn>:<port>` with Negotiate; which one is used (event 4624 logon process), and does sealing work with both?

## S3 — Reanimation over LDAP

1. Recycle Bin **off** (default in the lab): delete `lab-user05`, then reanimate over LDAP (delete `isDeleted`, replace `distinguishedName` with the Show-Deleted control). Which attributes are left (tombstone)? Memberships?
2. Recycle Bin **on** (`ad_recycle_bin = true` or `Enable-ADOptionalFeature`): the same with `lab-user06`. Are all attributes and links back?
3. The right "Reanimate Tombstones" on the domain for a delegated account (not a domain admin).
4. A recycled object (Recycle Bin on, past the deleted-object lifetime): does the reanimation fail (unwillingToPerform)? Then the product should re-create it instead.
5. A reanimated tombstone user: is `userAccountControl` kept, and may it be enabled without a password? (The product writes the account step itself.)
6. Re-created user: does the add with `userAccountControl` | ACCOUNTDISABLE and without a password work under the default password policy?

## S4 — Passwords

`unicodePwd` over LDAP 389 with Negotiate signing and sealing (no LDAPS in the lab): set it for a reanimated user. Error codes if sealing is off.

## S5 — VM generation id

1. VM restore of dc1 to another location, **without** power on, isolated network: XAPI `VM.get_generation_id` before (original) and after (copy). Expected: a new id (xen-api `import.ml`: fresh genid for every metadata import that is not a live migration).
2. Original location with "replace": the same check.
3. Start the copy in an isolated network: event 2179/2180 (VM generation id changed), new invocationId (`repadmin /showrepl` → "DSA invocationID"), RID pool dropped (`dcdiag /test:ridmanager`).

## S6 — GPO versions and DFS-R

1. Change "Lab Policy A" (registry marker), restore the old GPT folder by hand, raise `versionNumber` (GPC) and `Version` (GPT.INI) to max + 1.
2. dc2 (after `ad_replica`): does DFS-R replicate the folder? `gpresult /r` on a member: the old setting is back.
3. The product's swap: the staging folder `{GUID}.bdfu-new` in SYSVOL replicates to dc2 for a moment, then the renames and the delete of `{GUID}.bdfu-replaced`: does DFS-R handle the renames cleanly (no `{GUID}` conflict folders, event log DFS Replication)?
4. The DACL written to the GPC with the SD flags control (DACL only): GPMC shows the delegation as in the backup, and `gpresult` on a member applies the policy.
5. Re-created GPC (policy gone): GPMC shows the policy with its name and settings; "Lab Policy C" (unlinked) and the links of A and B.

## Results

| Spike | Date | Result |
|---|---|---|
| S1 | 2026-11-08 | The VSS freeze ran out (snapshot 28.1 s on shared LVM with two disks, 10.6–12.6 s with one), so no backup was recorded. Instead the agent copies the directory with `ntdsutil "activate instance ntds" ifm "create full …"` before the snapshot (2.5 s for 96 MB, its own shadow copy, clean shutdown): `repadmin /showbackup` shows the copy's time (`dSASignature`), no 2089, dsamain opens the copy without recovery. Event 1917 came only for the first copy. A later VSS full of the VM snapshot (NTDS *Stable*, 7.7 s) did not raise the `dSASignature` version again. |
| S2 | 2026-11-08 | dsamain opens a copy from a VSS snapshot after `esentutl /r` ("consistent"); `/logpath` and `/allowUpgrade` are accepted; Negotiate bind as the domain admin over the DC's FQDN works; queries fast. Ports and firewall (2026-11-09, H15): dsamain listens on 0.0.0.0, reachable from another machine when the DC's firewall is off (as on dc1). Without `-allowNonAdminAccess` only Domain Admins and Enterprise Admins get data: a normal domain account binds, reads rootDSE, and gets `OperationsError (1) 000004DC … a successful bind must be completed` on every search; with the switch it reads the backup with the backup's ACLs. Live, a normal account can read the `CN=Deleted Objects` container itself but lists none of its objects (Administrator: 32), so tombstones are invisible to it. Decision: domain admins only, checked when the explore opens. |
| S3 | 2026-11-08 | Without the Recycle Bin (Windows Server 2025, forest level 2025): a deleted object has `isDeleted` and `isRecycled` at once; reanimation with the Show-Deleted modify works, the tombstone kept sAMAccountName and objectSid. With the Recycle Bin: open. |
| S4 | 2026-11-08 | `unicodePwd` over the sealed Negotiate connection works; enabling the account after it works too. |
| S5 | 2026-11-09 | Not checkable with dc1: its VM has no generation id (`generation_id` empty from its template "Windows Server 2025 cloudbase-init"; `msDS-GenerationId` of BDFU-LAB-DC1 empty, so Windows never saw one). A restore to another location (`import_metadata` with `restore=false`) left the copy without one as well: XAPI only makes a fresh id when the VM has one. The field is read-only after creation. Consequence: a warning in every backup of such a DC, a warning in the restore wizard and session; the guide asks for DCs from a template with a generation id. Steps 2 and 3 need such a DC (and an isolated network for 3). With dc2 (template rebuilt with a generation id): a copy restored to another location got a new id (logged old and new), and powered on in an isolated network Windows logged 2168 and 2170 (old value = dc2's at backup time, new value = the copy's) and took a new invocation id. Original location with replace (step 2) not run. |
| S6 | | |
