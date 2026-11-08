# Standards for Active Directory Backup and Object-Level Restore

There are no formal standards yet (`agent-os/standards/index.yml` is empty). These conventions apply:
- the foundation spec (`../2026-11-01-2134-xcpng-backup-foundation/standards.md`);
- the guest agent spec (`../2026-11-02-1430-windows-guest-agent/standards.md`);
- the FLR additions (`../2026-11-02-1713-file-level-restore/standards.md`): untrusted guest data, fail loudly, threads, audit, dependencies;
- the SQL restore additions (`../2026-11-03-0026-sql-server-restore/standards.md`): secrets, destructive steps, log lines.

The additions below apply on top.

---

## Additions for this spec

### Documented interfaces only
- **Reading the backup:** the directory in a backup is read through `dsamain.exe` (AD Database Mounting Tool) and LDAP.
- **Writing:** the live directory is changed over LDAP only. Allowed operations: reanimation with the Show-Deleted control, add, modify, ModifyDN; plus the SD-flags control for security descriptors.
- **Never:** ntds.dit is never parsed by our own code, and nothing writes into a database file.
- **Tools:** `esentutl` and `dsamain` are Windows' own tools, called by the agent with fixed arguments. Paths are quoted and checked; no names from the backup go into a command line.

### Writes into a live directory
- **Preview first:** the restore dialog lists every step before anything is written (reanimate, re-create, the attributes with old → new values, memberships, GPO folder, versions, links), with a confirmation naming the live domain and DC.
- **Nothing is deleted:** v1 never deletes a live object. Attribute values are replaced or added, and a GPT folder is swapped only with the previous folder kept aside until the restore is done.
- **Order and results:**
  - parents before children; per object one result (done, skipped with the reason, or failed with the LDAP error and its DN);
  - one failed object does not stop the others;
  - the summary counts them.
- **Re-created objects:** the warning says that SID and GUID are new and that ACLs, group references in other domains and SID-based assignments do not follow.

### Secrets
- **Passwords** entered for a restore travel like account passwords (in memory, in the job request only). They are written with `unicodePwd` over a sealed or LDAPS connection, and never logged or stored.
- **The ntds.dit copy** holds every password hash of the domain:
  - **where:** it goes only into `%ProgramData%\backdafuckup\ad\<session>` on the target DC (ACL: SYSTEM and Administrators);
  - **never elsewhere:** it is never sent anywhere else and never read for secrets;
  - **cleanup:** it is deleted when the session ends, at the idle timeout and at agent start, from a journal. A failed delete is an error in the log, naming the path.
- **The backup's SYSTEM hive** (boot key) is never transferred.

### Log lines
- **Every write is named:** the DN, the operation, the attributes, and old → new values for non-secret attributes, plus the account used.
- **Errors** name the DC (host, IP, port), the DN and the LDAP result code with its text.
