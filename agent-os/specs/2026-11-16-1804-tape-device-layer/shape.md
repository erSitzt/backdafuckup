# Tape Device Layer — Shaping Notes

## Scope

backdafuckup drives tape libraries and standalone drives on its own. It works with:
- SCSI commands built from the public standards;
- transports for Windows and Linux;
- a library model with locks and inventory;
- a Linux tape agent for libraries attached to another host;
- a fake library for tests;
- a VTL lab.

The console can inventory, load, unload, import, export and label. Tape jobs (Part B) write and read through the block pump.

## Decisions

### By the user (2026-11-16)
- **Tape out on an HPE MSL** (roadmap). The production library is an **HPE MSL6480 with LTO-9**.
- **Vendor-neutral code.** Standalone drives without a changer are supported.
- **Device access both ways:**
  - directly on the backup server;
  - through a remote tape agent.
- **Public standards only.** No vendor tools or SDKs; nothing taken from purchased products. Open-source licences are fine.
- **Encryption as an option:** software and LTO hardware encryption.
- **WORM tapes are supported.**
- **Lab:** the Holo VTL (MIT) emulating the MSL6480 with LTO-9 drives, built by me with OpenTofu. It only creates, and it is never torn down without asking.
- **UI:** keep it simple and intuitive. Veeam, Proxmox Backup Server and other tape software are inspiration, from their public docs only. Several mockup variants were drawn for the user to choose from.

### In the design (2026-11-16)
- **One project** `Backdafuckup.Tape`, shared by the server, the tape agent and the recovery command.
- **The agent forwards CDBs; all logic stays on the server.**
- **Block mode:** variable blocks, 256 KiB by default (the Microsoft iSCSI initiator's default limit, readable everywhere), up to 1 MiB per pool.
- **End of tape** is found by EOM sense or by polling the EOP bit of READ POSITION (Holo has no early-warning sense). Commands that move the tape are never retried blindly.
- **Devices are identified by serial,** never by node; DVCID maps drives to elements.
- **Windows:** pass-through through the class device when a driver claims it, otherwise through the adapter. No vendor driver is needed.
- **Linux:** SG_IO on `/dev/sg*`, never `/dev/st*` (which rewinds on close). Group `tape`, no root.

## Context

- **Visuals:** mockups in a design canvas, with three variants of the tape page (slot map, tables, tree with details) plus the pool dialog, the tape job editor, the restore from tape and the operator prompt. They are not stored in the repository.

  **Chosen by the user (2026-11-16): variant B**, tables with filters as in Proxmox Backup Server:
  - the tape page has the tabs *Libraries & drives*, *Media* and *Media pools*;
  - drives are a compact table (drive, library, state, cartridge, job, speed);
  - media are a table with search, pool and location chips, and the actions in a row menu.

  The dialogs below the variants (media pool, tape job editor, restore from tape, operator banner, restore points marked *on tape*) apply as drawn.
- **References:** see `references.md`.
- **Product alignment:**
  - mission: "Works with … iSCSI block storage and tape", public documented APIs only;
  - tech stack: "Tape (Phase 2): HPE MSL tape library (LTO)";
  - roadmap Phase 2: "Tape out".
