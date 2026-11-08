# Active Directory Backup and Object-Level Restore — Shaping Notes

## Scope

Domain controllers are backed up application-aware, and objects come back out of their restore points without a VM restore.

**In v1:**
- **DC awareness:**
  - the backup recognises a DC, stores its AD facts with the restore point and records the backup in AD (VSS full);
  - the console marks such restore points and offers "Active Directory objects" as a restore.
- **Browse and compare:**
  - the backup's directory is opened on a live DC of the same domain (dsamain);
  - every object, attribute and membership can be searched and compared with the live AD;
  - each object shows its state: unchanged, changed, deleted, or gone.
- **Deleted objects back,** with and without the AD Recycle Bin:
  - reanimated with their objectGUID and SID, and the tombstone's missing attributes and memberships restored from the backup;
  - objects that no longer have a tombstone are re-created with a new SID and GUID, with a warning.
- **Changed attributes back:** chosen attributes of existing objects, including links (by GUID) and the location (OU).
- **GPOs back:**
  - the GPC in AD and the GPT in SYSVOL, with permissions;
  - versions raised so that clients and DFS-R pick them up;
  - links optional.
- **Safe DC VM restore:** a restored DC VM gets a new VM generation id (non-authoritative restore by Windows), and the wizard warns.

**Not in v1:**
- passwords or SID history from the backup;
- cross-domain restore and WMI filters;
- AD-integrated DNS zones and the configuration partition;
- authoritative restore through the product and forest recovery.

## Decisions (user, 2026-11-06)

- **Scope v1:**
  - "Deleted objects back";
  - "Changed attributes back";
  - "GPOs".
- **Recycle Bin:** enabled in the user's forest, but other customers may have it disabled, so both work and the product detects which applies.
- **Passwords:** a new password is set at restore. When the object comes back without one (tombstone, re-created), the admin enters a password, or the account stays disabled with "must change at next logon". No hashes are taken from the backup.
- **Reading the backup:**
  - **where:** in a Windows VM through the agent, on a live DC of the domain;
  - **how:** with **dsamain + LDAP**. Microsoft's documented AD Database Mounting Tool serves the backup's ntds.dit read-only over LDAP. This follows the integration policy "publicly available, documented APIs only". The internal layout of ntds.dit is not documented, and Veeam and Quest use their own readers.
- **DC backups:** **VSS full for DCs**, so AD records the backup and event 2089 stops. Copy-only stays for all other VMs. A DC that also has our SQL log backups stays copy-only, with a warning.
- **Objects without a tombstone:** **re-create** them with a new SID and GUID, as Veeam does. The UI and the log say clearly that permissions and references do not follow.
- **Lab:** a **lab forest via OpenTofu** (test forest, two DCs). There are no DCs yet. Nothing is ever written to `corp.example`.
- **References:** the SQL pieces.
- **Visuals:** none.

## How other products do it (public documentation, 2026-11-06)

- **Veeam Explorer for AD:**
  - **Deleted objects:** restores them from tombstones or the Recycle Bin on the target DC. Objects without either come back with a new objectSID and objectGUID.
  - **Passwords:** restores them, using the backup's SYSTEM hive.
  - **GPOs:** restores GPC and GPT, in the Enterprise editions only.
  - **Limits:** original domain only; no DC objects.
  - **Business-critical objects without a tombstone:** recommends an authoritative restore from an old DC backup.
- **Quest Recovery Manager for AD:**
  - **Agentless:** tombstone reanimation, then the attributes the tombstone lacks are re-applied over ADSI. Passwords and SID history are not possible this way.
  - **With its DC agent:** writes into the database directly. An object comes back only while its tombstone exists.
- **Microsoft:**
  - `dsamain` serves a backup or snapshot over LDAP for comparison;
  - objects come back with the Recycle Bin, ldp tombstone reanimation, or an authoritative restore.

## Context

- **Visuals:** none.
- **References:**
  - the SQL browser: restore point mount, registry hives, guest metadata;
  - the SQL session runners: restore, publish, recovery;
  - the agent's SQL job framework;
  - the file-level reader and the restore-files transfer.
- **Product alignment:**
  - roadmap phase 1 "Active Directory objects: application-aware AD backup with object-level restore";
  - mission "Application-aware (consistent backups of AD and MS SQL Server)" and "only publicly available, documented APIs".

## Standards Applied

See `standards.md`: the conventions of the earlier specs plus the AD additions (writes into a live directory, secrets, the work folder).
