# Deploying to a Windows server

`bdfu_deploy.py` installs and updates backdafuckup on a Windows Server from Linux: files over SMB (`C$`), commands over
WinRM (port 5985, NTLM with message encryption). Nothing has to be enabled on the server beyond the defaults.

Setup (once, on the machine you deploy from):

```bash
python3 -m venv --system-site-packages scripts/deploy/.venv
scripts/deploy/.venv/bin/pip install smbprotocol pywinrm
mkdir -p ~/.config/backdafuckup && $EDITOR ~/.config/backdafuckup/backup01.env   # BDFU_DEPLOY_USER, BDFU_DEPLOY_PASSWORD
chmod 600 ~/.config/backdafuckup/backup01.env
```

Generated passwords (PostgreSQL) go to `~/.config/backdafuckup/backup01.secrets` (mode 600); they are never printed.

| Command | What it does |
|---|---|
| `check [--reach host:port …]` | Read-only: OS, disks, services, ports, firewall, reachability (e.g. pool master `:443`, NBD `:10809`). |
| `postgres` | PostgreSQL 17 (EDB binaries) as service `postgresql-17`, local only, scram-sha-256, role and database `backdafuckup`. |
| `copy-repo --source DIR --dest C:\…` | Copies a repository folder as a tar stream over SMB and unpacks it with `tar.exe`. |
| `restore-db --dump FILE [--sql …]` | Restores a `pg_dump -Fc` into the empty catalog, then runs SQL (paths, server address). |
| `install --publish DIR [--data DIR]` | Installs or updates: files to `C:\Program Files\backdafuckup` (swapped as a whole), config and data in `C:\ProgramData\backdafuckup` (SYSTEM and Administrators only), service `backdafuckup` (depends on PostgreSQL), firewall rule 9555, health check. Refuses to restart while sessions run unless `--force`. `--data` takes over the key ring and certificate of an existing installation. |

Update after a change: `scripts/deploy/update-windows.sh` (builds win-x64 with the agent package and runs `install`).

## backup01.corp.example (since 2026-11-02)

- Windows Server 2025, 2 vCPU, 24 GB RAM, C: 100 GB. Web console `https://backup01.corp.example:9555/`.
- PostgreSQL 17.11 in `C:\Program Files\PostgreSQL\17`, data in `C:\ProgramData\PostgreSQL\17\data`.
- Repository `Test1` in `C:\backdafuckup\repositories\Test1` (moved from the development instance, 6 restore points).
- The pool (10.16.1.231/.232) is reachable on 443 (XAPI, HTTP export); NBD 10809 is not reachable from there.
