# Standards for Instant VM Recovery (vSphere)

No standards are indexed in `agent-os/standards/` yet. The conventions of the earlier specs apply:
- restore points are write-once;
- readers pin restore points;
- the manifest is the commit point.

The additions of the vSphere provider spec apply as well (`../2026-11-18-1713-vsphere-provider/standards.md`):
- only public interfaces;
- never destroy what we did not create;
- a ledger row before every external call;
- errors name the place.

## Additions for this spec

### Protocols from the RFCs
- NFSv3, MOUNT v3, ONC RPC and the portmapper are implemented from RFC 1813, 5531 and 1833.
- Other NFS servers serve only as behaviour references.

### A restore point is never written
- All writes go to the session's overlay.
- The restore point stays pinned while a session uses it, also across server restarts (catalog check plus re-pin).

### Durable before acknowledged
- Namespace changes are journalled and fsynced before the reply.
- COMMIT makes data durable before the bitmap.
- The write verifier changes at each process start, so a client resends what was not committed.

### Stable across restarts
- File handles, fsid and fileids never change for a running session.
- Handles are signed (HMAC) so they cannot be forged.

### Destroy only what is entirely ours
- Stop destroys a VM only if all its files are on our datastore.
- Otherwise it refuses and raises an alert.
