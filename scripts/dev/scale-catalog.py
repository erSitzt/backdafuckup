#!/usr/bin/env python3
"""Fills a SCRATCH catalog with many VMs for checking the console at scale (Backups & Restore).

Writes SQL to stdout: a fake pool, 13 jobs (daily, weekly, one without schedule, one disabled), N VMs with 7-72 restore
points each (1-4 disks, some domain controllers and SQL Server VMs, about 2 % that missed their last run), 20 VMs also in
a monthly archive job, 5 job members without a restore point and the restore points of a deleted job.

The restore points have no data in the repository: browsing, restoring or verifying them fails. Never run this against a
catalog that is in use.

    python3 scripts/dev/scale-catalog.py <repository id> [--vms 280] > scale.sql
    psql -h localhost -U backdafuckup -d <scratch database> -v ON_ERROR_STOP=1 -f scale.sql
"""
import argparse
import datetime
import json
import random
import uuid

parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
parser.add_argument("repository", help="id of a repository in the scratch catalog (repositories.id)")
parser.add_argument("--vms", type=int, default=280)
args = parser.parse_args()
random.seed(7)
now = datetime.datetime.now(datetime.timezone.utc).replace(microsecond=0)
CRED, CONN = "00000000-0000-0000-0000-00000000c001", "00000000-0000-0000-0000-00000000c002"


def uid():
    return str(uuid.UUID(int=random.getrandbits(128), version=4))


def esc(value):
    if value is None:
        return r"\N"
    if isinstance(value, bool):
        return "t" if value else "f"
    return str(value).replace("\\", "\\\\").replace("\t", " ").replace("\n", " ")


# name, kept points, days between runs, schedule kind, enabled
JOBS = [("Prod-SQL-Nightly", 30, 1, "Daily", True), ("Prod-Web-Nightly", 14, 1, "Daily", True), ("Fileserver-Nightly", 30, 1, "Daily", True),
        ("Domain-Controllers", 14, 1, "Daily", True), ("Citrix-Weekly", 8, 7, "Weekly", True), ("Dev-Weekly", 7, 7, "Weekly", True),
        ("ERP-Hourly", 72, 1 / 24, "Daily", True), ("Branch-Offices", 14, 1, "Daily", True), ("Exchange", 30, 1, "Daily", True),
        ("Monitoring", 7, 1, "Daily", True), ("Linux-Infra", 14, 1, "Daily", True), ("Test-Lab", 7, 1, "Daily", False)]
ARCHIVE = ("Monthly-Archive", 12, 30, "None", True)
PREFIXES = ["srv-sql", "srv-web", "srv-file", "srv-dc", "srv-ctx", "vm-dev", "srv-erp", "br", "srv-exch", "srv-mon", "lnx", "test"]
jobs = {j[0]: (uid(),) + j[1:] for j in JOBS + [ARCHIVE]}
points, disks, members = [], [], {name: [] for name in jobs}
seen = {name: 0 for name in jobs}

for i in range(args.vms):
    j = i % len(JOBS)
    name = f"{PREFIXES[j]}-{i:04d}"
    native = uid()
    sizes = [random.choice([40, 60, 80, 120, 250, 500, 1000]) << 30 for _ in range(random.choice([1, 1, 2, 2, 3, 4]))]
    job_names = [JOBS[j][0]] + (["Monthly-Archive"] if i >= args.vms - 20 else [])
    dc = JOBS[j][0] == "Domain-Controllers"
    sql = JOBS[j][0] in ("Prod-SQL-Nightly", "ERP-Hourly") and i % 3 == 0
    descriptor = {"Name": name, "Nics": [{"Mac": f"aa:bb:cc:{i // 256 % 256:02x}:{i % 256:02x}:{k:02x}", "Mtu": 1500, "Device": str(k), "NetworkId": uid(),
                                          "NetworkName": f"VLAN {100 + k}"} for k in range(random.choice([1, 1, 2]))],
                  "VmId": native, "VCpus": random.choice([2, 4, 8]), "OsName": None, "HasVtpm": False, "Firmware": "uefi", "SecureBoot": False,
                  "Description": "", "MemoryBytes": random.choice([4, 8, 16, 32]) << 30}
    for job_name in job_names:
        job_id, keep, every, _, _ = jobs[job_name]
        seen[job_name] += 1
        deleted = job_name == "Test-Lab" and seen[job_name] <= 8
        if not deleted:
            members[job_name].append((native, name))
        for k in range(keep):
            created = now - datetime.timedelta(days=k * every, minutes=random.randrange(0, 50))
            if i % 47 == 5:
                created -= datetime.timedelta(days=9)
            point = uid()
            full = k == keep - 1
            app = random.random() < 0.8
            reason = None if app or random.random() < 0.7 else f"Guest processing failed: the agent on {name} did not answer within 60 s."
            new = int(sum(sizes) * (random.uniform(0.3, 0.6) if full else random.uniform(0.002, 0.03)))
            points.append([point, args.repository, None if deleted else job_id, "Old-Infra" if deleted else job_name, None, CONN, "xen-scale", native, name,
                           created.isoformat(), "Full" if full else "Incremental", "Available", sum(sizes), new, f"restore-points/{point}.json",
                           json.dumps(descriptor), "application" if app else "crash", reason, random.randint(1, 12) if sql else 0, True if sql else None,
                           dc, "corp.example.local" if dc else None])
            for d, size in enumerate(sizes):
                disks.append([uid(), point, f"xvd{chr(97 + d)}", uid(), f"{name}-disk{d}", size, "sr-1", "storage-1", "Full" if full else "Cbt",
                              None, None, None, int(size * 0.4), new // len(sizes)])

for n in range(1, 6):
    members["Prod-Web-Nightly"].append((uid(), f"srv-web-new-{n}"))

print("-- Generated by scripts/dev/scale-catalog.py for a SCRATCH catalog: restore points without data.")
print("begin;")
print(f"insert into credentials (id, username, protected_secret, created_utc, updated_utc, kind) values ('{CRED}', 'root', 'not-a-secret', now(), now(), 'Hypervisor');")
print(f"insert into connections (id, kind, display_name, address, port, credential_id, trust_mode, allow_insecure_nbd, status) "
      f"values ('{CONN}', 'XenServer', 'Scale pool (fake)', 'xcp-scale.invalid', 443, '{CRED}', 'Pinned', false, 'Connected');")
for name, (job_id, _, _, kind, enabled) in jobs.items():
    days = 1 if kind == "Weekly" else 0
    print(f"insert into jobs (id, name, enabled, connection_id, repository_id, retention_points, schedule_kind, schedule_time_of_day, schedule_days_of_week, "
          f"schedule_time_zone_id, max_parallel_vms, nbd_connections_per_disk, retry_count, retry_delay_minutes) values ('{job_id}', '{name}', {str(enabled).lower()}, "
          f"'{CONN}', '{args.repository}', 14, '{kind}', time '22:00', {days}, 'Europe/Berlin', 4, 4, 2, 5);")
    for order, (native, vm) in enumerate(members[name]):
        print(f"insert into job_objects (id, job_id, native_id, cached_name, \"order\") values ('{uid()}', '{job_id}', '{native}', '{vm}', {order});")
print("copy restore_points (id, repository_id, job_id, job_name, session_id, connection_id, platform_id, vm_native_id, vm_name, created_utc, kind, state, "
      "logical_bytes, new_stored_bytes, manifest_path, vm, consistency, consistency_reason, sql_database_count, sql_backup_sets_recorded, domain_controller, ad_domain) from stdin;")
for row in points:
    print("\t".join(esc(v) for v in row))
print("\\.")
print("copy restore_point_disks (id, restore_point_id, disk_key, source_disk_id, name, capacity_bytes, storage_id, storage_name, mode, full_reason, "
      "change_token_kind, change_token_value, read_bytes, new_stored_bytes) from stdin;")
for row in disks:
    print("\t".join(esc(v) for v in row))
print("\\.")
print("commit;")
print("analyze restore_points;")
print(f"-- {len(points)} restore points, {len(disks)} disks, {args.vms} VMs (+5 without a restore point), {len(jobs)} jobs.")
