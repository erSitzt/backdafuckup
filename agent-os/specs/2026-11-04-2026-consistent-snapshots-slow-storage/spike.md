# Spikes

## 2a — T-SQL snapshot backup on SQL Server Express (2026-11-04, bdfu-lab-sql1)

SQL side only, over WinRM, with no snapshot in the pool. The instance is SQL Server 2025 Express (17.0.1000.7, EngineEdition 4) with the lab databases `shop` (FULL, `AUTO_CLOSE` on), `small` (SIMPLE, `AUTO_CLOSE` on) and `imoltp` (memory-optimized file group). Scripts: `spike-2a.ps1` and `spike-2a-part2.ps1` in the session scratchpad.

### Results

- **Express supports it.** `ALTER SERVER CONFIGURATION SET SUSPEND_FOR_SNAPSHOT_BACKUP = ON (MODE = COPY_ONLY)` was accepted in 13–14 ms.
- **The differential bitmap stays valid.** `sys.dm_server_suspend_status` shows `is_diff_map_cleared = 0` and `is_write_io_frozen = 1` per database.
- **Reads go on, writes wait, with no 10 s limit.** A read returned in 2–9 ms. Writes blocked during the suspend and still after 15 s. A write whose client timeout had fired was committed after the resume; client timeouts fire, but data is not lost.
- **The metadata backup:**
  - `BACKUP SERVER TO DISK = … WITH METADATA_ONLY, FORMAT, MEDIADESCRIPTION = …` took 73 ms and wrote a `.bkm` of 18 KB.
  - `msdb.dbo.backupset` has one row per suspended database: `type = D`, `is_copy_only = 1`, `is_snapshot = 1`, with LSNs.
  - `RESTORE HEADERONLY` and `FILELISTONLY … WITH METADATA_ONLY` read it back. `FILELISTONLY` with `DBNAME` filters it, and lists the memory-optimized container of `imoltp` (type S).
- **The SQL Server service account writes the `.bkm` itself.** The agent's own folders are not writable for it (OS error 5). The instance's backup folder (`C:\SQLBackup` in the lab) works. A failed `BACKUP` also resumes the writes.
- **`AUTO_CLOSE` databases:**
  - The server-wide suspend only covers databases that are open at that moment. `shop` or `small`, when closed by `AUTO_CLOSE`, was left out.
  - `ALTER DATABASE shop SET SUSPEND_FOR_SNAPSHOT_BACKUP = ON (MODE = COPY_ONLY)` opens and suspends a closed database, and `BACKUP DATABASE shop … METADATA_ONLY` works.
- **`GROUP = (shop, small, imoltp)` fails** with "Atleast one database was previously suspended for snapshot backup". It failed in a fresh process without connection pooling too. SQL Server suspended `shop`, then rolled it back.
- **When the suspending session ends** (connection closed, no pooling) without a `BACKUP`, SQL Server resumes all databases by itself within 2 s, and writes work again.
- **Error log:**
  - "… acquired suspend locks in session N", "I/O is frozen on database X", "… successfully resumed in session N", "I/O was resumed on database X", "Database backed up … first LSN … last LSN".
  - Support can read the pause from these lines.

### Finding while building step 2 (SQL Server 2022 container, 2026-11-04)

- **A session that has suspended one database cannot suspend another.** The second `ALTER DATABASE … SUSPEND_FOR_SNAPSHOT_BACKUP = ON` on the same connection fails with "Atleast one 'database' was previously suspended for snapshot backup".
  - This is the same error as `GROUP`, and it explains the failure seen in the lab.
- So the agent suspends every database **on its own unpooled connection**, and records it on that connection with `BACKUP DATABASE … WITH METADATA_ONLY`. The records go into one media set per instance: the first with `FORMAT`, the rest appended.
- Closing a database's connection resumes exactly that database: the dead-man switch works per database. Tested: the connections were disposed while suspended, SQL Server resumed, and `msdb` had no entries.

### Consequences for step 2

1. **Suspend per database, not `GROUP`, each on its own connection (see the finding above):**
   - The agent runs `ALTER DATABASE [x] SET SUSPEND_FOR_SNAPSHOT_BACKUP = ON (MODE = COPY_ONLY)` for each user database it backs up. Each one takes milliseconds, and closed `AUTO_CLOSE` databases are included.
   - After the snapshot it runs `BACKUP DATABASE [x] TO DISK = … WITH METADATA_ONLY` for each database into one media set: the first with `FORMAT`, the others appended.
   - To check: several database-level suspends in one session, and the 64-database limit for database-level suspends.
2. **A dedicated, unpooled connection is the dead-man switch.**
   - If the agent dies or loses the connection, SQL Server resumes by itself.
   - If the agent hangs with the connection alive, the agent's own timer closes the connection.
   - The server never takes the snapshot after the agent's deadline.
3. **The `.bkm` goes to the instance's backup folder** (registry `BackupDirectory`), or to an agent folder with an ACE for the service SID `NT SERVICE\MSSQL$<instance>`. The agent reads it, uploads it, and deletes it.
4. **The write pause equals the snapshot time**: 25–47 s on `alletra-mp02` for the lab VMs. Applications with the usual 30 s command timeout (SqlClient default) see timeouts during that time.
   - The job must show the pause, warn above a limit, and offer the shortenings from step 1.
   - The admin guide must say this plainly.
5. **Per-database consistency:**
   - "application (SQL snapshot)" for every database with a `msdb` row from this backup.
   - "crash" with the reason for databases that could not be suspended.
