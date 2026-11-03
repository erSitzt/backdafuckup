#!/usr/bin/env python3
"""
Deploys and updates backdafuckup on a Windows server: files over SMB (C$), commands over WinRM (PowerShell, NTLM with
message encryption). Credentials come from an env file outside the repository:

    BDFU_DEPLOY_USER=DOMAIN\\admin
    BDFU_DEPLOY_PASSWORD=...

Usage (from the repository root, with scripts/deploy/.venv):
    scripts/deploy/.venv/bin/python scripts/deploy/bdfu_deploy.py --host backup01.corp.example check
"""
import argparse
import os
import pathlib
import secrets
import subprocess
import sys
import time
import urllib.request
import zipfile

import smbclient
import winrm

DEFAULT_ENV = pathlib.Path.home() / ".config" / "backdafuckup" / "backup01.env"
REPO = pathlib.Path(__file__).resolve().parents[2]
CACHE = pathlib.Path.home() / ".cache" / "backdafuckup-deploy"
STAGING = r"C:\ProgramData\backdafuckup-deploy"
PG_VERSION = "17.11"
PG_HOME = r"C:\Program Files\PostgreSQL\17"
PG_DATA = r"C:\ProgramData\PostgreSQL\17\data"
PG_SERVICE = "postgresql-17"
INSTALL_DIR = r"C:\Program Files\backdafuckup"
DATA_DIR = r"C:\ProgramData\backdafuckup"
SERVICE = "backdafuckup"


def secrets_file(env_path: pathlib.Path) -> pathlib.Path:
    return env_path.with_suffix(".secrets")


def load_secrets(env_path: pathlib.Path) -> dict:
    path = secrets_file(env_path)
    return load_env_values(path) if path.exists() else {}


def save_secrets(env_path: pathlib.Path, values: dict) -> None:
    path = secrets_file(env_path)
    current = load_secrets(env_path)
    current.update(values)
    path.write_text("".join(f"{k}={v}\n" for k, v in current.items()), encoding="utf-8")
    path.chmod(0o600)


def load_env_values(path: pathlib.Path) -> dict:
    values = {}
    for line in path.read_text(encoding="utf-8").splitlines():
        line = line.strip()
        if line and not line.startswith("#") and "=" in line:
            key, value = line.split("=", 1)
            values[key.strip()] = value.strip().strip('"').strip("'")
    return values


def ps_quote(text: str) -> str:
    """A PowerShell single-quoted string."""
    return "'" + text.replace("'", "''") + "'"


def load_env(path: pathlib.Path) -> dict:
    if not path.exists():
        sys.exit(f"{path} is missing (BDFU_DEPLOY_USER, BDFU_DEPLOY_PASSWORD).")
    if path.stat().st_mode & 0o077:
        print(f"warning: {path} is readable by others; chmod 600 it.", file=sys.stderr)
    values = load_env_values(path)
    for key in ("BDFU_DEPLOY_USER", "BDFU_DEPLOY_PASSWORD"):
        if not values.get(key):
            sys.exit(f"{path} has no {key}.")
    return values


class Target:
    """A Windows server reached over WinRM (5985) and SMB (445)."""

    def __init__(self, host: str, user: str, password: str):
        self.host = host
        self.user = user
        self._session = winrm.Session(f"http://{host}:5985/wsman", auth=(user, password), transport="ntlm", message_encryption="always")
        smbclient.register_session(host, username=user, password=password)

    def ps(self, script: str, check: bool = True) -> str:
        """
        Runs PowerShell; raises with the error text if it fails. Long scripts go to a .ps1 file over SMB, because the
        encoded command line of run_ps is limited to about 8000 characters.
        """
        if len(script) > 1500:
            return self._ps_file(script, check)
        result = self._session.run_ps(script)
        out = result.std_out.decode("utf-8", "replace")
        err = result.std_err.decode("utf-8", "replace")
        if check and (result.status_code != 0 or "<S S=\"Error\">" in err):
            raise RuntimeError(f"PowerShell on {self.host} failed ({result.status_code}):\n{out}\n{clean_clixml(err)}")
        return out

    def _ps_file(self, script: str, check: bool) -> str:
        import uuid
        self.mkdir(STAGING)
        path = f"{STAGING}\\run-{uuid.uuid4().hex}.ps1"
        with smbclient.open_file(self.unc(path), mode="wb") as file:
            file.write(b"\xef\xbb\xbf" + script.encode("utf-8"))
        try:
            result = self._session.run_cmd("powershell.exe", ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", path])
        finally:
            try:
                smbclient.remove(self.unc(path))
            except OSError:
                pass
        out = result.std_out.decode("utf-8", "replace")
        err = result.std_err.decode("utf-8", "replace")
        if check and result.status_code != 0:
            raise RuntimeError(f"PowerShell on {self.host} failed ({result.status_code}):\n{out}\n{err}")
        return out

    def unc(self, windows_path: str) -> str:
        """C:\\x\\y -> \\\\host\\C$\\x\\y"""
        drive, rest = windows_path[0], windows_path[3:]
        return f"\\\\{self.host}\\{drive}$\\{rest}"

    def put(self, local: pathlib.Path, windows_path: str) -> None:
        """Copies a local file to the server, with progress for large files."""
        size = local.stat().st_size
        done = 0
        last = -1
        with open(local, "rb") as source, smbclient.open_file(self.unc(windows_path), mode="wb") as target:
            while True:
                chunk = source.read(4 << 20)
                if not chunk:
                    break
                target.write(chunk)
                done += len(chunk)
                percent = done * 100 // max(size, 1)
                if size > 50 << 20 and percent // 10 != last // 10:
                    print(f"  {local.name}: {percent}% of {size / (1 << 20):.0f} MiB", flush=True)
                    last = percent

    def put_stream(self, stream, windows_path: str, label: str) -> int:
        """Copies a stream (e.g. a tar pipe) to the server; returns the bytes written."""
        done = 0
        next_report = 1 << 30
        with smbclient.open_file(self.unc(windows_path), mode="wb") as target:
            while True:
                chunk = stream.read(4 << 20)
                if not chunk:
                    break
                target.write(chunk)
                done += len(chunk)
                if done >= next_report:
                    print(f"  {label}: {done / (1 << 30):.0f} GiB", flush=True)
                    next_report += 1 << 30
        return done

    def mkdir(self, windows_path: str) -> None:
        smbclient.makedirs(self.unc(windows_path), exist_ok=True)


def clean_clixml(text: str) -> str:
    """PowerShell errors over WinRM arrive as CLIXML; keep the readable parts."""
    if not text.startswith("#< CLIXML"):
        return text
    import re
    parts = re.findall(r"<S S=\"Error\">(.*?)</S>", text, re.S)
    return "".join(parts).replace("_x000D__x000A_", "\n").strip()


def cmd_check(target: Target, args) -> None:
    print(target.ps(r"""
$os = Get-CimInstance Win32_OperatingSystem
"OS:        $($os.Caption) $($os.Version), $([math]::Round($os.TotalVisibleMemorySize/1MB,1)) GB RAM, $([math]::Round($os.FreePhysicalMemory/1MB,1)) GB free"
"Host:      $env:COMPUTERNAME ($((Get-CimInstance Win32_ComputerSystem).Domain)), $((Get-CimInstance Win32_Processor | Measure-Object -Property NumberOfLogicalProcessors -Sum).Sum) logical CPUs"
Get-CimInstance Win32_LogicalDisk -Filter "DriveType=3" | ForEach-Object { "Disk:      $($_.DeviceID) $([math]::Round($_.Size/1GB)) GB, $([math]::Round($_.FreeSpace/1GB)) GB free" }
Get-NetIPAddress -AddressFamily IPv4 | Where-Object { $_.IPAddress -ne '127.0.0.1' } | ForEach-Object { "IP:        $($_.IPAddress)/$($_.PrefixLength) ($($_.InterfaceAlias))" }
$svc = Get-Service -Name backdafuckup,postgresql* -ErrorAction SilentlyContinue
if ($svc) { $svc | ForEach-Object { "Service:   $($_.Name) $($_.Status)" } } else { "Service:   no backdafuckup or PostgreSQL service" }
"Port 9555: $(if (Get-NetTCPConnection -LocalPort 9555 -State Listen -ErrorAction SilentlyContinue) { 'in use' } else { 'free' })"
"Port 5432: $(if (Get-NetTCPConnection -LocalPort 5432 -State Listen -ErrorAction SilentlyContinue) { 'in use' } else { 'free' })"
"VC++ x64:  $((Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x64' -ErrorAction SilentlyContinue).Version)"
"tar.exe:   $(if (Get-Command tar.exe -ErrorAction SilentlyContinue) { 'yes' } else { 'no' })"
"Firewall:  $((Get-NetFirewallProfile | ForEach-Object { "$($_.Name)=$($_.Enabled)" }) -join ', ')"
"""))
    for host in args.reach or []:
        name, _, port = host.partition(":")
        print(target.ps(f"$r = Test-NetConnection -ComputerName '{name}' -Port {int(port or 443)} -WarningAction SilentlyContinue; \"Reach:     {name}:{port or 443} -> $($r.TcpTestSucceeded) ($($r.RemoteAddress))\"").strip())


def cmd_postgres(target: Target, args) -> None:
    """PostgreSQL from the EDB binaries as a service, local only, scram-sha-256, with a database for backdafuckup."""
    existing = target.ps(f"(Get-Service -Name {PG_SERVICE} -ErrorAction SilentlyContinue).Status").strip()
    stored = load_secrets(args.env)
    if existing:
        print(f"{PG_SERVICE} already exists ({existing}); nothing to install.")
        return

    CACHE.mkdir(parents=True, exist_ok=True)
    archive = CACHE / f"postgresql-{PG_VERSION}-1-windows-x64-binaries.zip"
    if not archive.exists():
        url = f"https://get.enterprisedb.com/postgresql/{archive.name}"
        print(f"Downloading {url} …", flush=True)
        urllib.request.urlretrieve(url, archive.with_suffix(".part"))
        archive.with_suffix(".part").rename(archive)

    superuser = stored.get("BDFU_PG_SUPERUSER_PASSWORD") or secrets.token_urlsafe(24)
    app = stored.get("BDFU_PG_PASSWORD") or secrets.token_urlsafe(24)
    save_secrets(args.env, {"BDFU_PG_SUPERUSER_PASSWORD": superuser, "BDFU_PG_PASSWORD": app})

    target.mkdir(STAGING)
    print(f"Copying {archive.name} to {target.host} …", flush=True)
    target.put(archive, STAGING + "\\postgresql.zip")
    print("Installing PostgreSQL …", flush=True)
    print(target.ps(rf"""
$ErrorActionPreference = 'Stop'
$stage = {ps_quote(STAGING)}
$home17 = {ps_quote(PG_HOME)}
$data = {ps_quote(PG_DATA)}
if (-not (Test-Path (Join-Path $home17 'bin\postgres.exe'))) {{
    New-Item -ItemType Directory -Force -Path (Split-Path $home17) | Out-Null
    $extract = Join-Path $stage 'pgsql-extract'
    if (Test-Path $extract) {{ Remove-Item -Recurse -Force $extract }}
    New-Item -ItemType Directory -Force -Path $extract | Out-Null
    tar.exe -xf (Join-Path $stage 'postgresql.zip') -C $extract
    if ($LASTEXITCODE -ne 0) {{ throw "tar failed with $LASTEXITCODE" }}
    Move-Item (Join-Path $extract 'pgsql') $home17
    Remove-Item -Recurse -Force $extract
}}
& (Join-Path $home17 'bin\postgres.exe') --version
New-Item -ItemType Directory -Force -Path $data | Out-Null
icacls $data /grant 'NT AUTHORITY\NetworkService:(OI)(CI)F' /T /Q | Out-Null
$pw = Join-Path $stage 'pw.txt'
Set-Content -Path $pw -Value {ps_quote(superuser)} -NoNewline -Encoding ascii
try {{
    & (Join-Path $home17 'bin\initdb.exe') -D $data -U postgres --pwfile=$pw -E UTF8 --locale=C --auth=scram-sha-256 2>&1 | Select-Object -Last 3
    if ($LASTEXITCODE -ne 0) {{ throw "initdb failed with $LASTEXITCODE" }}
}} finally {{ Remove-Item -Force $pw }}
icacls $data /grant 'NT AUTHORITY\NetworkService:(OI)(CI)F' /T /Q | Out-Null
& (Join-Path $home17 'bin\pg_ctl.exe') register -N {PG_SERVICE} -D $data -U 'NT AUTHORITY\NetworkService' -S auto
if ($LASTEXITCODE -ne 0) {{ throw "pg_ctl register failed with $LASTEXITCODE" }}
Start-Service {PG_SERVICE}
Start-Sleep -Seconds 3
$env:PGPASSWORD = {ps_quote(superuser)}
$psql = Join-Path $home17 'bin\psql.exe'
& $psql -h localhost -U postgres -d postgres -v ON_ERROR_STOP=1 -c "CREATE ROLE backdafuckup LOGIN PASSWORD '{app}';" -c "CREATE DATABASE backdafuckup OWNER backdafuckup;"
if ($LASTEXITCODE -ne 0) {{ throw "creating the database failed with $LASTEXITCODE" }}
Remove-Item Env:PGPASSWORD
Remove-Item -Force (Join-Path $stage 'postgresql.zip')
"PostgreSQL: $((Get-Service {PG_SERVICE}).Status), listening on $((Get-NetTCPConnection -LocalPort 5432 -State Listen | ForEach-Object LocalAddress | Sort-Object -Unique) -join ', ')"
"""))


def psql(sql: str, database: str = "backdafuckup", superuser_password: str = "") -> str:
    """A psql call on the server, as PowerShell (the password goes through the environment, not the command line)."""
    return rf"""
$env:PGPASSWORD = {ps_quote(superuser_password)}
& {ps_quote(PG_HOME + "\\bin\\psql.exe")} -h localhost -U postgres -d {database} -v ON_ERROR_STOP=1 -tA -c {ps_quote(sql)}
$code = $LASTEXITCODE
Remove-Item Env:PGPASSWORD
if ($code -ne 0) {{ throw "psql failed with $code" }}
"""


def running_sessions(target: Target, args) -> int:
    """Sessions that a restart would break (0 if the catalog is not there yet)."""
    secret = load_secrets(args.env).get("BDFU_PG_SUPERUSER_PASSWORD", "")
    out = target.ps(psql("select count(*) from information_schema.tables where table_name = 'sessions'", superuser_password=secret)).strip()
    if out != "1":
        return 0
    return int(target.ps(psql("select count(*) from sessions where state in ('Running','Queued','Stopping')", superuser_password=secret)).strip() or "0")


def cmd_copy_repo(target: Target, args) -> None:
    """Streams a repository folder as tar over SMB and unpacks it with tar.exe on the server."""
    source = pathlib.Path(args.source)
    files = sum(1 for p in source.rglob("*") if p.is_file())
    print(f"Copying {source} ({files} files) to {args.dest} on {target.host} …", flush=True)
    target.mkdir(STAGING)
    tar = subprocess.Popen(["tar", "-cf", "-", "-C", str(source), "."], stdout=subprocess.PIPE)
    size = target.put_stream(tar.stdout, STAGING + "\\repo.tar", source.name)
    if tar.wait() != 0:
        raise RuntimeError("tar failed")
    print(f"  {size / (1 << 30):.1f} GiB copied; unpacking …", flush=True)
    print(target.ps(rf"""
$ErrorActionPreference = 'Stop'
$dest = {ps_quote(args.dest)}
if ((Test-Path $dest) -and (Get-ChildItem $dest -Force | Select-Object -First 1)) {{ throw "$dest is not empty" }}
New-Item -ItemType Directory -Force -Path $dest | Out-Null
tar.exe -xf {ps_quote(STAGING + "\\repo.tar")} -C $dest
if ($LASTEXITCODE -ne 0) {{ throw "tar failed with $LASTEXITCODE" }}
Remove-Item -Force {ps_quote(STAGING + "\\repo.tar")}
$all = Get-ChildItem $dest -Recurse -File -Force
"Unpacked: $($all.Count) files, $([math]::Round(($all | Measure-Object Length -Sum).Sum / 1GB, 2)) GiB in $dest"
""").strip())
    print(f"Expected: {files} files.")


def cmd_restore_db(target: Target, args) -> None:
    """Restores a pg_dump (custom format) into the empty backdafuckup database, then runs the given SQL statements."""
    secret = load_secrets(args.env)["BDFU_PG_SUPERUSER_PASSWORD"]
    tables = target.ps(psql("select count(*) from information_schema.tables where table_schema = 'public'", superuser_password=secret)).strip()
    if tables != "0" and not args.force:
        raise SystemExit(f"The database on {target.host} already has {tables} tables; use --force to restore over it.")
    target.mkdir(STAGING)
    target.put(pathlib.Path(args.dump), STAGING + "\\catalog.dump")
    print(target.ps(rf"""
$ErrorActionPreference = 'Stop'
$env:PGPASSWORD = {ps_quote(secret)}
& {ps_quote(PG_HOME + "\\bin\\pg_restore.exe")} -h localhost -U postgres -d backdafuckup --no-owner --role=backdafuckup --exit-on-error {ps_quote(STAGING + "\\catalog.dump")} 2>&1
$code = $LASTEXITCODE
Remove-Item Env:PGPASSWORD
Remove-Item -Force {ps_quote(STAGING + "\\catalog.dump")}
if ($code -ne 0) {{ throw "pg_restore failed with $code" }}
"restored"
""").strip())
    for statement in args.sql or []:
        print(f"SQL: {statement} -> {target.ps(psql(statement, superuser_password=secret)).strip()}")


def cmd_install(target: Target, args) -> None:
    """Installs or updates the server: files to Program Files, config and data to ProgramData, the Windows service."""
    if (count := running_sessions(target, args)) and not args.force:
        raise SystemExit(f"{count} session(s) are running on {target.host}; not restarting (use --force).")
    publish = pathlib.Path(args.publish)
    archive = CACHE / "backdafuckup-win-x64.zip"
    CACHE.mkdir(parents=True, exist_ok=True)
    with zipfile.ZipFile(archive, "w", zipfile.ZIP_DEFLATED) as zip_file:
        for path in publish.rglob("*"):
            if path.is_file():
                zip_file.write(path, path.relative_to(publish).as_posix())
    target.mkdir(STAGING)
    print(f"Copying {archive.name} ({archive.stat().st_size / (1 << 20):.0f} MiB) …", flush=True)
    target.put(archive, STAGING + "\\backdafuckup.zip")

    secret = load_secrets(args.env)
    target.mkdir(DATA_DIR)
    if args.data:
        # Key ring and certificate of an existing installation (never overwritten).
        data = pathlib.Path(args.data)
        for path in sorted(data.rglob("*")):
            if path.is_file() and path.relative_to(data).parts[0] in ("keys", "server-certificate.pfx"):
                relative = "\\".join(path.relative_to(data).parts)
                remote = DATA_DIR + "\\" + relative
                if not smbclient.path.exists(target.unc(remote)):
                    target.mkdir(remote.rsplit("\\", 1)[0])
                    target.put(path, remote)
                    print(f"  {relative} copied")

    settings = {
        "ConnectionStrings": {"Catalog": f"Host=localhost;Database=backdafuckup;Username=backdafuckup;Password={secret['BDFU_PG_PASSWORD']}"},
        "Server": {"HttpsPort": 9555},
    }
    import json
    print(target.ps(rf"""
$ErrorActionPreference = 'Stop'
$data = {ps_quote(DATA_DIR)}
$config = Join-Path $data 'appsettings.json'
if (-not (Test-Path $config)) {{
    Set-Content -Path $config -Value {ps_quote(json.dumps(settings, indent=2))} -Encoding utf8
    "appsettings.json written"
}}
# Only SYSTEM and administrators: the folder holds the catalog password, the key ring and the certificate.
icacls $data /inheritance:r /grant:r 'NT AUTHORITY\SYSTEM:(OI)(CI)F' 'BUILTIN\Administrators:(OI)(CI)F' /T /Q | Out-Null

$install = {ps_quote(INSTALL_DIR)}
$new = "$install.new"
if (Test-Path $new) {{ Remove-Item -Recurse -Force $new }}
New-Item -ItemType Directory -Force -Path $new | Out-Null
tar.exe -xf {ps_quote(STAGING + "\\backdafuckup.zip")} -C $new
if ($LASTEXITCODE -ne 0) {{ throw "unpacking failed with $LASTEXITCODE" }}
Remove-Item -Force {ps_quote(STAGING + "\\backdafuckup.zip")}

$service = Get-Service -Name {SERVICE} -ErrorAction SilentlyContinue
if ($service -and $service.Status -ne 'Stopped') {{
    $processId = (Get-CimInstance Win32_Service -Filter "Name='{SERVICE}'").ProcessId
    Stop-Service -Name {SERVICE} -Force
    (Get-Service -Name {SERVICE}).WaitForStatus('Stopped', [TimeSpan]::FromSeconds(60))
    # "Stopped" comes before the process has released its files: wait for it to exit.
    if ($processId -gt 0) {{ Wait-Process -Id $processId -Timeout 60 -ErrorAction SilentlyContinue }}
}}
# Swap the program folders. A file still in use (e.g. an Explorer window in the folder) gets 60 s; if the swap fails,
# the old version is put back and started again, so a failed update never leaves the server stopped.
$swapped = $false
try {{
    if (Test-Path $install) {{
        if (Test-Path "$install.old") {{ Remove-Item -Recurse -Force "$install.old" }}
        for ($attempt = 1; $attempt -le 30 -and -not $swapped; $attempt++) {{
            try {{ Rename-Item $install "$install.old" -ErrorAction Stop; $swapped = $true }} catch {{ Start-Sleep -Seconds 2 }}
        }}
        if (-not $swapped) {{ throw "$install is still in use after 60 s (an Explorer window, console or editor open in it?); the update was not installed." }}
    }}
    Rename-Item $new $install -ErrorAction Stop
}}
catch {{
    if ($swapped -and -not (Test-Path $install)) {{ Rename-Item "$install.old" $install }}
    if ($service) {{ Start-Service -Name {SERVICE} }}
    throw
}}
if (-not $service) {{
    sc.exe create {SERVICE} binPath= "`"$install\backdafuckup.exe`"" start= delayed-auto DisplayName= "backdafuckup backup server" | Out-Null
    sc.exe description {SERVICE} "Open-source VM backup for XCP-ng / XenServer" | Out-Null
    sc.exe failure {SERVICE} reset= 86400 actions= restart/10000/restart/60000/restart/300000 | Out-Null
    sc.exe config {SERVICE} depend= {PG_SERVICE} | Out-Null
    "service created"
}}
if (-not (Get-NetFirewallRule -DisplayName 'backdafuckup 9555' -ErrorAction SilentlyContinue)) {{
    New-NetFirewallRule -DisplayName 'backdafuckup 9555' -Direction Inbound -Protocol TCP -LocalPort 9555 -Action Allow | Out-Null
    "firewall rule added"
}}
Start-Service -Name {SERVICE}
"service: $((Get-Service -Name {SERVICE}).Status)"
""").strip())

    url = f"https://{target.host}:9555/health"
    import ssl
    context = ssl.create_default_context()
    context.check_hostname = False
    context.verify_mode = ssl.CERT_NONE
    for _ in range(60):
        try:
            with urllib.request.urlopen(url, context=context, timeout=5) as response:
                print(f"{url}: {response.status}")
                break
        except Exception:
            time.sleep(3)
    else:
        print(f"{url} did not answer within 3 minutes; see {DATA_DIR}\\logs on the server.")
    target.ps(rf"if (Test-Path '{INSTALL_DIR}.old') {{ Remove-Item -Recurse -Force '{INSTALL_DIR}.old' }}", check=False)


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--host", required=True)
    parser.add_argument("--env", type=pathlib.Path, default=DEFAULT_ENV)
    sub = parser.add_subparsers(dest="command", required=True)
    check = sub.add_parser("check", help="read-only: OS, disks, services, ports, reachability")
    check.add_argument("--reach", nargs="*", help="host:port the server must reach, e.g. the XCP-ng pool master")
    sub.add_parser("postgres", help="install PostgreSQL as a service with a database for backdafuckup")
    copy_repo = sub.add_parser("copy-repo", help="copy a repository folder (tar over SMB)")
    copy_repo.add_argument("--source", required=True)
    copy_repo.add_argument("--dest", required=True, help=r"e.g. C:\backdafuckup\repositories\Test1")
    restore_db = sub.add_parser("restore-db", help="restore a pg_dump into the empty catalog")
    restore_db.add_argument("--dump", required=True)
    restore_db.add_argument("--sql", nargs="*", help="statements to run afterwards (paths, server URL)")
    restore_db.add_argument("--force", action="store_true")
    install = sub.add_parser("install", help="install or update the server and its service")
    install.add_argument("--publish", required=True, help="dotnet publish output for win-x64")
    install.add_argument("--data", help="data folder of an existing installation (keys, certificate) to take over")
    install.add_argument("--force", action="store_true", help="restart even while sessions run")
    args = parser.parse_args()

    env = load_env(args.env)
    target = Target(args.host, env["BDFU_DEPLOY_USER"], env["BDFU_DEPLOY_PASSWORD"])
    commands = {"check": cmd_check, "postgres": cmd_postgres, "copy-repo": cmd_copy_repo, "restore-db": cmd_restore_db, "install": cmd_install}
    commands[args.command](target, args)


if __name__ == "__main__":
    main()
