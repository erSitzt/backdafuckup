#!/usr/bin/env python3
"""Creates the storage pool, library, drives, iSCSI ACL and cartridges through Holo's REST API, as the web UI does.
Idempotent: what exists already is left as it is.

Usage:       holo-bootstrap.py <bootstrap.json>
Environment: HOLO_URL (default http://127.0.0.1), HOLO_API_KEY or HOLO_API_KEY_FILE (default /etc/holo-terraform/api-key)
"""
import json
import os
import sys
import time
import urllib.error
import urllib.request

URL = os.environ.get("HOLO_URL", "http://127.0.0.1").rstrip("/")
ACTOR = "deploy-vtl"


def api_key():
    if os.environ.get("HOLO_API_KEY"):
        return os.environ["HOLO_API_KEY"]
    with open(os.environ.get("HOLO_API_KEY_FILE", "/etc/holo-terraform/api-key")) as fh:
        return fh.read().strip()


KEY = api_key()


class APIError(Exception):
    def __init__(self, msg, status=None):
        super().__init__(msg)
        self.status = status


def call(method, path, body=None, ok=(200, 201, 204)):
    data = None if body is None else json.dumps(body).encode()
    req = urllib.request.Request(URL + path, data=data, method=method)
    req.add_header("X-HOLO-API-Key", KEY)
    if data is not None:
        req.add_header("Content-Type", "application/json")
    try:
        with urllib.request.urlopen(req, timeout=120) as resp:
            raw = resp.read()
            status = resp.status
    except urllib.error.HTTPError as err:
        raw, status = err.read(), err.code
    if status not in ok:
        raise APIError(f"{method} {URL}{path} -> HTTP {status}: {raw.decode(errors='replace').strip()}", status)
    return json.loads(raw) if raw.strip() else None


def wait_api(timeout=600):
    # /healthz answers 503 while no data plane runs (e.g. without cartridges), so wait for the API itself.
    deadline = time.time() + timeout
    last = ""
    while time.time() < deadline:
        try:
            call("GET", "/v1/libraries")
            return
        except APIError as err:
            if err.status in (401, 403):
                raise APIError(f"The API key is refused: {err}") from err
            last = str(err)
        except OSError as err:  # still starting
            last = str(err)
        time.sleep(5)
    raise APIError(f"Holo's API at {URL} does not answer: {last}")


def ensure_pool(cfg):
    pool_id = cfg["pool_id"]
    pools = {p["poolId"]: p for p in call("GET", "/v1/storage/pools") or []}
    pool = pools.get(pool_id)
    if pool is None:
        pool = call("POST", "/v1/storage/pools", {"poolId": pool_id, "name": cfg["pool_name"], "actor": ACTOR})
        print(f"Pool '{pool_id}' created")
    if pool.get("disks"):
        print(f"Pool '{pool_id}' has {len(pool['disks'])} disk(s) already")
        return
    disks = (call("GET", "/v1/storage/disks/discovery") or {}).get("disks", [])
    free = [d for d in disks if d.get("availability") == "available"]
    for d in disks:
        print(f"  disk {d['devicePath']}: {d.get('sizeBytes', 0) // 2**30} GiB, {d.get('availability')} {d.get('unavailableReason', '')}".rstrip())
    if not free:
        print("WARNING: no free disk for the pool")
        return
    disk = max(free, key=lambda d: d.get("sizeBytes", 0))
    call("POST", f"/v1/storage/pools/{pool_id}/disks/attach", {"devicePath": disk["devicePath"], "actor": ACTOR})
    print(f"Disk {disk['devicePath']} attached to pool '{pool_id}'")


def ensure_library(cfg):
    lib = cfg["library"]
    lib_id = lib["libraryId"]
    if any(l["libraryId"] == lib_id for l in call("GET", "/v1/libraries") or []):
        print(f"Library '{lib_id}' exists already")
    else:
        call("POST", "/v1/libraries", lib)
        print(f"Library '{lib_id}' created ({lib['vendor']} {lib['libraryType']}, {lib['driveType']})")

    existing = {d["driveId"] for d in call("GET", "/v1/drives") or []}
    for idx in range(lib["driveCount"]):
        drive_id = f"{lib_id}-drv-{idx + 1:02d}"
        if drive_id in existing:
            continue
        call("POST", "/v1/drives", {"driveId": drive_id, "libraryId": lib_id, "slot": lib["driveStartAddress"] + idx})
        print(f"Drive '{drive_id}' created")


def ensure_acl(cfg):
    initiators = cfg.get("initiator_iqns") or []
    lib_id = cfg["library"]["libraryId"]
    if not initiators:
        print("No initiators given: every initiator may log in")
        return
    current = call("GET", f"/v1/libraries/{lib_id}/iscsi-security")["binding"]
    auth = current.get("auth") or {}
    if auth.get("mode") == "none" and sorted(auth.get("initiators") or []) == sorted(initiators):
        print("iSCSI ACL set already")
        return
    call("PUT", f"/v1/libraries/{lib_id}/iscsi-security", {
        "generation": int(current.get("generation") or 0) + 1,
        "auth": {"mode": "none", "initiators": initiators},
        "actor": ACTOR,
    })
    print(f"iSCSI ACL: only {', '.join(initiators)}")


def ensure_cartridges(cfg):
    lib_id = cfg["library"]["libraryId"]
    have = [c for c in call("GET", "/v1/cartridges") or [] if c.get("libraryId") == lib_id]
    missing = cfg["cartridge_count"] - len(have)
    for _ in range(max(missing, 0)):
        c = call("POST", "/v1/cartridges", cfg["cartridge"])
        print(f"Cartridge {c['barcode']} created")
    if missing <= 0:
        print(f"{len(have)} cartridges exist")


def show_targets():
    pubs = call("GET", "/v1/targets/publications") or []
    print("iSCSI targets:")
    for p in sorted(pubs, key=lambda p: p["targetIqn"]):
        print(f"  {p.get('state', '?'):9} {p.get('deviceRole', '?'):8} {p['targetIqn']}")


def main():
    with open(sys.argv[1]) as fh:
        cfg = json.load(fh)
    wait_api()
    ensure_pool(cfg)
    ensure_library(cfg)
    ensure_acl(cfg)
    ensure_cartridges(cfg)
    show_targets()


if __name__ == "__main__":
    try:
        main()
    except APIError as err:
        print(f"ERROR: {err}", file=sys.stderr)
        sys.exit(1)
