# HPE Alletra Storage MP B10000: array snapshots via WSAPI v3 for backup from storage snapshots (state: 2026-11-10)

Scope: the array and API side of "snapshot a LUN, export the snapshot read-only to a backup proxy over iSCSI, read it, unexport, delete". Setup: XCP-ng 8.3 pool with two 4 TiB iSCSI LUNs from a B10000 used as shared LVM SRs. Only public sources were used. hpe.com / psnow / support.hpe.com pages (QuickSpecs, WSAPI developer guide, CLI reference) timed out or returned 403 to the fetch tool in this session, so some HPE primary documents are cited only by link and their content could not be checked. Where that happened, it is noted under Gaps.

## 1. Licensing: is a separate licence needed for snapshots (Virtual Copy) or for the WSAPI?

### Takeaway
HPE sells B10000 software as an all-inclusive subscription, and its data sheet says this gives access to all OS features at no extra cost. Veeam's and Commvault's lists of a "Virtual Copy" licence (and "Virtual Lock" for immutable snapshots) come from 3PAR, where these were separately licensed features. On a B10000 they are features covered by the subscription, not extra purchases. However, I could not open an HPE QuickSpecs table that names Virtual Copy line by line. No source mentions a separate WSAPI licence.

### Cited Findings
- HPE's German B10000 data sheet (PSN1014727563DEDE, August 2025) says: "Nutzen Sie das volle Potenzial von B10000 mit unserem All-inclusive-Softwareabonnement. Erhalten Sie Zugriff auf alle Betriebssystemfunktionen und -verbesserungen, einschließlich des KI-gestützten Cloud-Betriebs auf der HPE GreenLake Cloud, ganz ohne zusätzliche Kosten." ("Get the full potential of B10000 with our all-inclusive software subscription. Get access to all operating system features and enhancements, including AI-driven cloud operations on HPE GreenLake cloud, at no additional cost.") It also says "Dank SaaS-basierter Bereitstellung stehen Ihnen neue Data Services sofort zur Verfügung" ("Thanks to SaaS-based delivery, new data services are available to you immediately"). — [HPE data sheet (DE), mirror at acp-gruppe.com](https://www.acp-gruppe.com/hubfs/HPE%20Alletra%20Storage%20MP%20B10000-Datenblatt-PSN1014727563DEDE.pdf)
- An English data sheet mirror (public tender portal) has the same wording: "all-inclusive software subscription ... access to all operating system features and enhancements ... at no additional cost" (seen as a search snippet only). — [tender mirror of HPE data sheet](https://storage.mtender.gov.md/get/f61ab102-181b-4bb4-98f7-b5969cf8a747-1755776140972)
- B10000 software is sold as per-TB subscription SKUs (for example "HPE Alletra Storage MP B10000 per TB 5-year Software and Support SaaS", S3Q02AAE; 4-year subscription licences at CDW). — [Lambda-tek listing](https://www.lambda-tek.com/HPE-S3Q02AAE-shUS/B49536624?viewSpec=y); [CDW listing](https://www.cdw.com/product/hpe-alletra-storage-mp-b10000-subscription-license-4-years-support/8449890)
- An HPE presenter deck (partner material) says the B10000 ships with "all-inclusive system software licenses". This is indicative, not an official licence statement. — [SlideShare: M1 – HPE Alletra Storage MP B10000 presenter](https://www.slideshare.net/slideshow/m1-hpe-alletra-storage-mp-b10000-presenter-pptx/285599886)
- Veeam Backup & Replication 13 user guide, page "HPE Alletra Storage MP B10000, 9000, Primera, 3PAR" (updated 2025-10-30, build 13.1.2.57), for snapshot orchestration and backup from storage snapshots with retention: "Enable HPE Web Services API server. Veeam Backup & Replication uses the HPE Web Services API server to work with the HPE Alletra Storage MP B10000, 9000, Primera, 3PAR storage system." and "A license for the storage system must support Virtual Copy." For immutability: "Make sure that the license on the storage system supports HPE Virtual Lock and that the VVRetentionTimeMax parameter is specified." — [Veeam Help Center](https://helpcenter.veeam.com/docs/vbr/userguide/storage_limitations_primera.html)
- Commvault 11.42 system requirements for "HPE Alletra Storage MP / Primera / Alletra 9000 / Alletra Storage MP B10000" list Snapshot, Clone and Virtual Copy licences, plus "Install the Virtual Lock license for immutable snapshots". Remote Copy is not listed as required for snapshots. Supported array OS versions are 10.2.0, 10.3.0, 10.4.0 and 10.5.0. — [Commvault docs](https://docs.commvault.com/11.42/software/system_requirements_for_hpe_alletra_storage_mp_primera_hpe_alletra_9000_series_hpe_alletra_storage_mp_b10000_storage_array.html)
- Older Veeam forum threads (3PAR era) say only the Virtual Copy (snapshots) licence was required, with Remote Copy needed only for replicating to other arrays. — [Veeam forum: 3PAR integration](https://forums.veeam.com/vmware-vsphere-f24/3par-integration-t26221.html)
- No source found states a separate licence for the WSAPI. The OpenStack Cinder Alletra MP driver, HPE FlowKit and Veeam only require the WSAPI service to be enabled and running. — [OpenStack Cinder HPE Alletra MP driver (2026.2)](https://docs.openstack.org/cinder/2026.2/configuration/block-storage/drivers/hpe-alletramp-driver.html); [hpe-storage-flowkit-py on PyPI](https://pypi.org/project/hpe-storage-flowkit-py/)

### Inferences
- "Virtual Copy" (snapshots) and "Virtual Lock" (snapshot retention/immutability) are inherited 3PAR/Primera feature names. On 3PAR they were separately licensed. Veeam and Commvault keep one requirements page for the whole family, which is why their wording reads like a separate purchase. Given HPE's statement that the subscription covers "all operating system features", a correctly subscribed B10000 should meet the "licence must support Virtual Copy" requirement without buying anything extra. Confirm on the array with the licence display: the 3PAR-lineage CLI command is `showlicense`, but I did not verify it on B10000 docs in this session.
- Our product does not need Virtual Lock and should not use it for temporary backup snapshots (see section 2: retention makes snapshots undeletable).

### Gaps
- I could not fetch the B10000 QuickSpecs (hpe.com/psnow/doc/a50006985enw; the fetch timed out), so I could not quote its software section listing the included data services (Virtual Copy, Virtual Lock, Remote Copy, Peer Persistence, encryption). That is the definitive HPE source. I also found no separate HPE "licensing guide" for B10000.
- I found no public statement on whether the all-inclusive subscription also covers "secondary" or "spare" capacity, or whether a lapsed subscription disables data services such as snapshots.

## 2. WSAPI v3 operations: auth, snapshots (single and consistent multi-volume), read-only, expiration, export (host/VLUN), async tasks, removal, enablement and port, roles and auditing

### Takeaway
The flow is fully possible with documented public REST calls. Authenticate with POST /api/v3/credentials and use the returned key as a Bearer token. Snapshot a volume with POST /api/v3/volumes/{uid} and action CREATE_SNAPSHOT_VOLUME (parameters readOnly, expireSecs, retainSecs). For consistent multi-LUN snapshots, snapshot an application set with POST /api/v3/applicationsets/{uid} and action CREATE_SNAPSHOT_APPSET. Export with POST /api/v3/vluns to a host object created with POST /api/v3/hosts (iscsiPaths). Long operations return a taskUri, which you poll at /api/v3/tasks/{id}. Remove with DELETE /vluns/{uid} and DELETE /volumes/{snapshot uid}. The WSAPI uses HTTPS on port 443 and must be enabled (`showwsapi` / `startwsapi`). The documented minimum role for provisioning-style use is "edit". I found no public confirmation of a custom role limited to snapshot plus export.

### Cited Findings
**Documentation entry points**
- HPE Developer's B10000 page links the "HPE Alletra Storage Web Services API Developer Guide" (https://www.hpe.com/support/AlletraMP-B10000-WSAPIV3-devguide), the "Web Services API Documentation" with the v3 OpenAPI spec (https://developer.hpe.com/ws/api-spec), a v3 FAQ (https://www.hpe.com/psnow/doc/a00148521enw) and the Slack channel #hpe-alletra-b10k-api. — [HPE Developer: HPE Alletra platform](https://developer.hpe.com/platform/hpe-alletra/home/)
- The OpenAPI page at developer.hpe.com/ws/api-spec is a client-side rendered (Gatsby/Redoc) page. Its static page data contains no spec, so the spec has to be loaded in a browser or downloaded there. The developer guide and FAQ on hpe.com timed out for the fetch tool. — [developer.hpe.com/ws/api-spec](https://developer.hpe.com/ws/api-spec)

**Endpoint, enablement, port**
- The Cinder Alletra MP driver (OpenStack 2026.2) says the WSAPI server "must be enabled and running". Check with `showwsapi` (sample output: HTTPS port 443, version 1.15.0) and start it with `startwsapi`. Config keys are `hpe3par_api_url = https://10.1.2.3/api/v1` and `hpe_api_url_v3 = https://10.1.2.3/api/v3` ("WSAPI V3 Server URL"). Required array OS is 10.5.0 or higher. — [OpenStack Cinder HPE Alletra MP driver](https://docs.openstack.org/cinder/2026.2/configuration/block-storage/drivers/hpe-alletramp-driver.html)
- BMC Discovery reaches the B10000 through the "embedded Web Services API v3" using `https://<IP or HOSTNAME>:<PORT>/api/v3/systems`, default port 443. It needs a user "with REST API access with at least read-only privilege". — [BMC Discovery: HPE Alletra MP B10000 (REST)](https://docs.bmc.com/xwiki/bin/view/IT-Operations-Management/Discovery/BMC-Discovery/Configipedia/Storage-Systems-Information/HPE-Alletra-MP-B10000-REST)
- Axonius (third-party) says enabling WSAPI requires a user with the Super or Service role, or a role with the `wsapi_set` right. This source is about "HPE Alletra SAN" in general. — [Axonius: HPE Alletra SAN](https://docs.axonius.com/pl/docs/hpe-alletra-san)
- Veeam lists "Enable HPE Web Services API server" as a prerequisite and documents doing it over SSH with administrator privileges. That page targets 3PAR/Primera. — [Veeam: Enable HPE Web Services API Server](https://helpcenter.veeam.com/docs/backup/vsphere/storage_configure_enable_web.html)

**Authentication / sessions (from HPE's Apache-2.0 FlowKit v3 client, v1.0.1)**
- Login: `POST /api/v3/credentials` with body `{"user": ..., "password": ...}`. The response field `key` is the session token, sent afterwards as `Authorization: Bearer <key>`. `GET /credentials` is used to validate a token, and `DELETE /credentials/{key}` ends a session. FlowKit caches a session for 14 minutes (`_SESSION_TIMEOUT = 14 * 60`) before creating a new one. — [hpe-storage-flowkit-py 1.0.1, file v3/core/session.py (PyPI sdist)](https://pypi.org/project/hpe-storage-flowkit-py/); [GitHub hpe-storage/hpe_storage_flowkit_py](https://github.com/hpe-storage/hpe_storage_flowkit_py)
- Collection queries use filters such as `GET /volumes?name=<name>`, `/applicationsets?appSetName=<name>` and `/vluns?volumeName=..&lun=..&hostName=..` with the request header `experimentalfilter: true`. Objects are addressed by `uid`. — [FlowKit v3 workflows/snapshot.py, vlun.py](https://pypi.org/project/hpe-storage-flowkit-py/)

**Snapshot of one volume**
- `POST /api/v3/volumes/{volumeUid}` with body `{"action": "CREATE_SNAPSHOT_VOLUME", "parameters": {"customName": "<name>", "namePattern": "CUSTOM", ...}}`. The validator lists the allowed parameters: `comment`, `customName`, `expireSecs` ("Expiration value specified for volume snapshot", uint64), `id`, `namePattern` (enum `PARENT_TIMESTAMP`, `PARENT_SEC_SINCE_EPOCH`, `CUSTOM`), `readOnly` ("If true snapshot mode is read only else read/write"), and `retainSecs` ("Retention value specified for volume snapshot"). — [FlowKit v3 workflows/snapshot.py and validators/snapshot_validator.py](https://pypi.org/project/hpe-storage-flowkit-py/)
- A snapshot is itself a volume object. Deletion is `DELETE /api/v3/volumes/{snapshotUid}`. Restore is `POST /volumes/{snapUid}` with `{"action": "PROMOTE_SNAPSHOT_VOLUME"}`. — [FlowKit v3 workflows/snapshot.py](https://pypi.org/project/hpe-storage-flowkit-py/)
- The older WSAPI v1 equivalent (python-3parclient, still maintained) is `POST /api/v1/volumes/{name}` with `{"action": "createSnapshot", "parameters": {"name", "readOnly", "expirationHours" ("time from now to expire"), "retentionHours", "comment", "id"}}`. Documented errors include `PERM_DENIED`, `NO_SNAP_CPG` ("No snapshot CPG has been configured for the volume"), `VV_LIMIT_REACHED` ("Maximum number of volumes reached") and `INV_OPERATION_VV_READONLY_TO_READONLY_SNAP` ("Creating a read-only copy from a read-only volume is not permitted"). — [python-3parclient client.py](https://github.com/hpe-storage/python-3parclient/blob/master/hpe3parclient/client.py)

**Consistent snapshot of several volumes**
- Application sets (the v3 successor of volume sets): create with `POST /api/v3/applicationsets` and body `{"appSetName", "appSetType", "members": [...], "domain", "comments"}`, change with `PATCH /applicationsets/{uid}`, delete with `DELETE`. Snapshot with `POST /api/v3/applicationsets/{uid}` and body `{"action": "CREATE_SNAPSHOT_APPSET", "parameters": {"snapshotName": "<name>", ...optional}}`. — [FlowKit v3 workflows/volumeset.py and snapshot.py](https://pypi.org/project/hpe-storage-flowkit-py/)
- v1 equivalent: `createSnapshotOfVolumeSet` with `readOnly`, `expirationHours` and `retentionHours`. The name uses the 3PAR "VV Name Patterns", so one name pattern produces one snapshot per member. Errors include `EMPTY_SET` and `NON_EXISTENT_SET`. — [python-3parclient client.py](https://github.com/hpe-storage/python-3parclient/blob/master/hpe3parclient/client.py)
- Commvault does not support replication on OS 10.4.0 "due to HPE changes to virtual volume sets and RCG". This indicates that the volume set model changed around 10.4. — [Commvault system requirements](https://docs.commvault.com/11.42/software/system_requirements_for_hpe_alletra_storage_mp_primera_hpe_alletra_9000_series_hpe_alletra_storage_mp_b10000_storage_array.html)
- The OpenStack Cinder Alletra MP driver supports "Create and delete snapshot group" and volume groups (consistency groups). — [OpenStack Cinder HPE Alletra MP driver](https://docs.openstack.org/cinder/2026.2/configuration/block-storage/drivers/hpe-alletramp-driver.html)

**Async tasks**
- Mutating calls may return `taskUri` (or `resourceUri`). Poll `GET /api/v3/tasks/{id}`. The field `status` is `STATE_FINISHED` on completion, with `state.overall == "STATE_NORMAL"` meaning success. `STATE_FAILED` and `STATE_CANCELLED` are terminal errors. `percentComplete` is also returned, and `GET /tasks` lists tasks. — [FlowKit v3 workflows/task.py, utils/utils.py handle_async_response](https://pypi.org/project/hpe-storage-flowkit-py/)

**Export (host, host set, VLUN)**
- Host: `POST /api/v3/hosts` with name plus optional `iscsiPaths` (list of IQNs), `fcPaths`, `nvmePaths`, `persona` (integer; FlowKit tests use 1 and 2), `domain`, `setName`, `transportType` (`UNKNOWN`/`FC`/`TCP`), `descriptors`, `keyValuePairs`, `isVvolHost`, `addPath` and `port`. `GET /hostpaths` lists initiator paths seen by the array. Host sets use `POST`/`PATCH`/`DELETE /api/v3/hostsets`. — [FlowKit v3 validators/host_validator.py, workflows/host.py, hostset.py](https://pypi.org/project/hpe-storage-flowkit-py/)
- VLUN (export): `POST /api/v3/vluns` with `{"volumeName", "hostName"}` and optional `autoLun`, `lun`, `maxAutoLun`, `noVcn`, `overrideLowerPriority` and `portPos` ("N:S:P"). Unexport: look up the VLUN uid with `GET /vluns?volumeName=..&lun=..&hostName=..`, then `DELETE /api/v3/vluns/{uid}`. — [FlowKit v3 workflows/vlun.py, validators/vlun_validator.py](https://pypi.org/project/hpe-storage-flowkit-py/)
- Veeam: for backup from storage snapshots, "Export types such as 'Hosts and port' and 'Port' are not supported", and Veeam creates "Host" or "Host set" exports instead. — [Veeam Help Center](https://helpcenter.veeam.com/docs/vbr/userguide/storage_limitations_primera.html)

**Read-only, expiration, retention**
- Veeam: "To show snapshots created by native means of a storage system ... as immutable, make sure that they are read-only and the retention time is specified for them." In other words, read-only plus retention gives immutability (Virtual Lock). — [Veeam Help Center](https://helpcenter.veeam.com/docs/vbr/userguide/storage_limitations_primera.html)
- Veeam community: immutable (Virtual Lock) snapshots "cannot be deleted until they expire — not even by Veeam", and early deletion requires HPE Support. — [Veeam community blog: backup architecture with B10000 and Veeam](https://community.veeam.com/blogs-and-podcasts-57/building-a-backup-architecture-with-hpe-alletra-storage-mp-b10000-and-veeam-9929)
- python-3parclient documents `expirationHours` as "time from now to expire" (the snapshot is removed automatically when it expires). — [python-3parclient client.py](https://github.com/hpe-storage/python-3parclient/blob/master/hpe3parclient/client.py)

**Roles, least privilege, audit**
- The Cinder Alletra MP driver requires an Alletra MP user with "the 'edit' role" for create, snapshot, export and delete. — [OpenStack Cinder HPE Alletra MP driver](https://docs.openstack.org/cinder/2026.2/configuration/block-storage/drivers/hpe-alletramp-driver.html)
- Discovery and monitoring work with read-only accounts. — [BMC Discovery](https://docs.bmc.com/xwiki/bin/view/IT-Operations-Management/Discovery/BMC-Discovery/Configipedia/Storage-Systems-Information/HPE-Alletra-MP-B10000-REST)
- A 3PAR user (community post, not vendor docs) reports that the built-in "create" role can create new LUNs, export them to hosts and browse, but cannot modify existing LUNs (for example resize). — [HPE Community: user privileges can create but not delete](https://community.hpe.com/t5/hpe-3par-storeserv-storage/user-privilges-can-create-new-objects-but-not-delete/m-p/7099603/highlight/true)
- Virtual Domains are supported by Veeam on these arrays: "If storage LUNs reside in a virtual domain, the proxy to which LUNs are exported must reside in the same virtual domain." — [Veeam Help Center](https://helpcenter.veeam.com/docs/vbr/userguide/storage_limitations_primera.html)

### Inferences
- Recommended flow for our product:
  1. Create a session.
  2. Resolve the base volume uid(s).
  3. For more than one LUN, create the snapshot through an application set containing both SR LUNs (CREATE_SNAPSHOT_APPSET); for one LUN, use CREATE_SNAPSHOT_VOLUME. Set `readOnly: true`, a unique `customName` with our tag, and `expireSecs` (for example backup window + margin) as the auto-delete safety net.
  4. Wait for the task.
  5. Create or reuse a host object for the proxy IQN (lowercase).
  6. POST /vluns (snapshot to proxy host).
  7. Rescan and read on the proxy.
  8. DELETE /vluns/{uid}.
  9. DELETE /volumes/{snapUid}.
  10. DELETE /credentials/{key}.
- Never set `retainSecs` on backup working snapshots. With Virtual Lock semantics the product could no longer delete them, which conflicts with the rule that the product cleans up its own tagged snapshots.
- `expireSecs` is the server-side safety net if the product crashes. Check whether the array refuses to delete a snapshot that still has an active VLUN; the 3PAR lineage generally does, but I did not verify this for v3. Either way, cleanup must unexport first.
- An application set snapshot is the natural way to get one point-in-time across both LUNs. A 3PAR set snapshot taken with one name pattern is consistent across members (3PAR lineage behaviour), but I found no B10000 document that states the consistency guarantee explicitly; see Gaps.
- The CREATE_SNAPSHOT_APPSET parameter list beyond `snapshotName` (whether `readOnly`/`expireSecs` are accepted per appset) is not visible in FlowKit, which passes optional parameters through unvalidated. Check against the OpenAPI spec.
- Least privilege: the documented baseline is "edit". Putting the SR LUNs and the proxy host into a dedicated Virtual Domain and giving the service account a domain-scoped role would limit what it can touch. Whether B10000 supports custom roles narrowed to snapshot + VLUN rights is not documented in what I could reach.
- Pin the session handling to the documented token lifetime. FlowKit's 14-minute cache suggests a 15-minute idle or absolute timeout, but this is inferred from client code, not from HPE documentation.

### Gaps
- The WSAPI v3 developer guide, FAQ and OpenAPI spec could not be read in this session (JS-rendered page; hpe.com timeouts). Not confirmed from HPE text:
  - exact session timeout and maximum concurrent sessions
  - full CREATE_SNAPSHOT_APPSET parameter schema
  - whether `expireSecs` requires a minimum value
  - the error returned when deleting an exported snapshot
  - HTTP status codes (202 vs 201) for async operations
- Whether WSAPI is enabled by default on a new B10000. Cinder and Veeam treat it as a prerequisite to check or enable. The B10000's own local UI and GreenLake management may depend on it, but no source confirms this.
- Custom roles or rights lists (`showrole`, `createrole`) for B10000 and the exact rights needed for snapshot + VLUN create/delete. HPE role docs (Primera ebook a00115977en_us_v4 "User roles") timed out.
- Audit logging of WSAPI calls (event log / audit role): no B10000 source found. The 3PAR lineage has an "audit" role and event log, but this was not verified for B10000.
- Explicit HPE statement that application set snapshots are crash-consistent across members.

## 3. Performance and capacity impact of snapshots; limits; creation time

### Takeaway
Alletra MP block snapshots (Virtual Copy) are metadata-based redirect-on-write snapshots. They avoid a copy-on-write write penalty and are very space-efficient for short-lived backup snapshots. HPE marketing claims "thousands" of read-only and read-write snapshots, but I found no public per-volume or per-system limit and no figure for creation time.

### Cited Findings
- Blocks & Files (April 2023, first look at Alletra Storage MP): snapshots use redirect-on-write (ROW), inherited from the Nimble lineage. Snapshots are a list of pointers, which avoids "a write performance penalty which persists in write-intensive environments". This is press coverage, not HPE documentation. — [Blocks & Files: Row, row, the Alletra MP boat](https://blocksandfiles.com/2023/04/12/row-row-the-alletra-mp-boat-block-and-file-extras/)
- HPE data reduction brochure: "Virtual Copy" is the Alletra Storage MP snapshot implementation. With efficient metadata handling "you can configure thousands of read-only and read-write snapshots". Thin persistence avoids allocating zero pages in volumes and read/write snapshots. Thin copy reclamation reclaims unused space from deleted Virtual Copy snapshots. — [HPE brochure "Store more for less" (CDW-hosted)](https://webobjects2.cdw.com/is/content/CDW/cdw/on-domain-cdw/brands/hewlett-packard-enterprise/brochure-store-more-for-less.pdf)
- The WSAPI error set includes `VV_LIMIT_REACHED` ("Maximum number of volumes reached") on snapshot creation, so snapshots count as volume objects against the system's volume limit. It also includes `NO_SNAP_CPG` (snapshot space must be configured; this is 3PAR/Primera semantics). — [python-3parclient client.py](https://github.com/hpe-storage/python-3parclient/blob/master/hpe3parclient/client.py)
- OpsRamp's B10000 integration exposes per-volume metrics "Number of snapshots taken per volume" and "hours since last snapshot taken for volume". — [OpsRamp: HPE Alletra 10K supported metrics](https://docs.opsramp.com/integrations/storage/hpe/hpe-alletra-10k/supported-metrics-and-default-monitoring-configuration/)
- Commvault (3PAR-era note): on 3.3.1 and later, "you might not be able to delete aged snapshots with child snapshots". This illustrates snapshot-tree dependencies; it is about 3PAR OS 3.3.1, not the B10000. — [Commvault considerations](https://documentation.commvault.com/11.42/commcell-console/considerations_for_hpe_alletra_storage_mp_primera_hpe_alletra_9000_series_hpe_alletra_storage_mp_b10000_storage_array.html)

### Inferences
- With ROW and a snapshot that lives only for the backup window, the capacity cost is roughly the blocks overwritten on the two SR LUNs during the backup, and the write-path penalty is negligible. The read load of the backup itself (up to 2 × 4 TiB on a full backup) is the real performance factor on the array and the iSCSI fabric.
- Snapshots consume volume IDs and objects. Leaked snapshots from crashed runs accumulate against the volume limit, which is one more reason to set `expireSecs` and run tag-based cleanup.
- Snapshot creation is a metadata operation and should normally take seconds. This is unverified: measure it against the task duration in `/tasks`.

### Gaps
- No public B10000 maximum for snapshots per volume, volumes per system, VLUNs or hosts was found. The QuickSpecs "configuration limits" section and the HPE B10000 architecture white paper could not be retrieved; a Scribd copy of the architecture white paper exists but was not used.
- No published figure for snapshot creation latency or for the performance impact of many snapshots on a B10000.
- How snapshot capacity is reported on B10000 (per-volume "snapshot used" vs system-level) was not confirmed.

## 4. Proxy access over iSCSI: connectivity, multipath, LVM duplicate visibility, HPE guidance for backup servers

### Takeaway
The proxy needs its own host object (lowercase IQN, appropriate persona) and a Host or Host set VLUN export of the snapshot only, never of the base LUNs. The only explicit B10000 caveats found are Veeam's: lowercase IQN, manual iSCSI target login on releases before 10.4, and no "port" export types. Use multipath (ALUA) on Windows and Linux. HPE's B10000-specific Windows and Linux implementation guides were not reachable. Because an XCP-ng SR LUN is an LVM PV, a snapshot carries the same PV and VG identity. Present it only to a proxy that does not also see the original. On Linux, handle the clone explicitly (for example vgimportclone, or our own read-only LVM/VHD parser).

### Cited Findings
- Veeam, for B10000 iSCSI:
  - "[For releases prior to 10.4] The proxy server initiator must be manually connected with the storage iSCSI target even though the host on the storage is created automatically."
  - "[For releases prior to 10.4] The proxy server IQN and Veeam iSCSI IQN used for host discovery must be registered with the same host on the storage."
  - "During the registration on the storage, the proxy server initiator IQN should be specified in lowercase, even if it is originally in uppercase."
  - Volumes exported to NVMe hosts may fail rescan.
  
  — [Veeam Help Center](https://helpcenter.veeam.com/docs/vbr/userguide/storage_limitations_primera.html)
- Commvault requires the iSCSI initiator to be configured on client and proxy computers to access the array. Array OS 10.2.0 "Supports only Linux and VMWare systems with iSCSI protocol". — [Commvault system requirements](https://docs.commvault.com/11.42/software/system_requirements_for_hpe_alletra_storage_mp_primera_hpe_alletra_9000_series_hpe_alletra_storage_mp_b10000_storage_array.html)
- Linux multipath: the upstream multipath-tools patch (2021) for HPE 3PAR/Primera/Alletra 9000 says the host setting should be "Generic-ALUA Persona 2 (UARepLun, SESLun, ALUA)". This is for the 3PAR lineage; B10000 was not explicitly covered. — [dm-devel: multipath-tools add info about HPE Alletra 6000 and 9000](https://listman.redhat.com/archives/dm-devel/2021-June/046812.html)
- Windows: an HPE Primera/3PAR host guide says that with persona 2, RTPG "automatically enables active/active multipathing for supported Windows host operating systems", and that for Windows Server 2019 hosts on some OS versions "use Windows Server 2016" as the host OS type. This is Primera-era documentation. — [HPE Primera docs: About hosts](https://www.hpe.com/psnow/resources/ebooks/a00114824en_us_v2/hosts_about.html)
- An HPE Community answer: Alletra MP iSCSI with ESXi Round Robin normally shows four active I/O paths. — [HPE Community: Alletra MP with iSCSI hosts](https://community.hpe.com/t5/hpe-primera-storage/alletra-mp-with-iscsi-hosts/m-p/7231153)
- An HPE Community thread on RHEL 8.10 points to an HPE document with sample multipath.conf settings for Linux hosts. — [HPE Community: RHEL 8.10 multipath.conf](https://community.hpe.com/t5/array-setup-and-networking/rhel-8-10-multipath-conf/m-p/7261706)
- HPE's B10000 integration training (course H41HPS) covers Windows, Linux, VMware and HPE Morpheus VM Essentials connectivity to B10000 arrays. This is evidence that HPE host connectivity guides exist; it is not the guide itself. — [HPE: B10000 Integration course](https://www.hpe.com/us/en/collaterals/collateral.a50014477enw.html)
- Morpheus (HPE VM Essentials) Alletra MP integration guide (search snippet; the page now returns 404): `multipath.conf` on each node should start with `defaults { find_multipaths yes ...`. — [Morpheus docs 8.0.4: HPE Alletra MP Storage](https://docs.morpheusdata.com/en/8.0.4/integration_guides/storage/hpe-alletra-mp.html)
- A Proxmox VE 9 cluster has been run against a B10000 over iSCSI with multipath (community thread; useful as a KVM/LVM analogue). — [Proxmox forum](https://forum.proxmox.com/threads/please-help-with-proxmox-ve-9-cluster-and-alletra-b10000-via-iscsi.171972/)
- Veeam: in virtual-domain setups, the proxy must be in the same domain as the LUNs, or outside any domain if the LUNs are outside. — [Veeam Help Center](https://helpcenter.veeam.com/docs/vbr/userguide/storage_limitations_primera.html)

### Inferences
- The snapshot is a separate volume with its own WWN, so at SCSI level it is a different LUN from the base. The XCP-ng pool will not see it unless it is exported to the pool hosts' host objects. Export only to the proxy's host object.
- LVM duplicate identity: an XCP-ng LVM SR LUN is a PV in VG `VG_XenStorage-<SR-uuid>`. The block-identical snapshot has the same PV UUID and VG name. On a Linux proxy that does not see the original LUN, it can be activated read-only. If the proxy can also see the original (for example the proxy is a pool member, or the base LUN is also exported to it), LVM reports duplicate PVs. In that case filter devices, or use `vgimportclone`, which exists for "duplicated PVs, e.g. created by a hardware snapshot" ([vgimportclone(8)](https://man7.org/linux/man-pages/man8/vgimportclone.8.html); not fetched in this session). Never activate it on an XCP-ng pool member. The safest design is a proxy that only reads the raw device (our own read-only LVM metadata + VHD parser) without LVM activation; this also covers Windows proxies, which cannot read LVM natively.
- Windows proxy: set the disk offline or read-only by policy (SAN policy) so Windows does not try to initialise the foreign LVM disk. Use MPIO with the inbox DSM; the B10000 persona for Windows is still to be confirmed from HPE's B10000 Windows host guide or SPOCK.
- A read-only export (`readOnly: true` snapshot) protects against accidental writes from the proxy at array level.

### Gaps
- The B10000-specific Windows Server and Linux host implementation guides (persona numbers, MPIO/multipath.conf stanzas, iSCSI timeouts) and SPOCK entries could not be retrieved. SPOCK requires an HPE login for some content.
- I found no HPE guidance specifically on presenting snapshots to backup servers.

## 5. Ecosystem: HPE's own integrations, third-party backup vendors using the B10000 API, and XenServer/XCP-ng

### Takeaway
Third-party storage-snapshot backup for B10000 exists from:
- Veeam (built into VBR 13; VMware vSphere and Veeam Agent only, explicitly not Hyper-V)
- Commvault IntelliSnap (REST-based "HPE Alletra Storage MP Snap engine")
- NAKIVO (VMware only)

HPE positions Veeam, Commvault and Zerto as partners. I found nothing for XenServer or XCP-ng, so our product would be the first.

### Cited Findings
- Veeam VBR 13 page for "HPE Alletra Storage MP B10000, 9000, Primera, 3PAR" covers snapshot orchestration, backup from storage snapshots, Virtual Lock immutability and Peer Persistence/Remote Copy. Its limitations cite "[For VMware and Veeam Agent integration]" for proxies. The integration is built into VBR, not a separate plug-in. — [Veeam Help Center](https://helpcenter.veeam.com/docs/vbr/userguide/storage_limitations_primera.html); [Veeam: HPE Alletra Storage MP B10000 listing](https://www.veeam.com/sys707)
- Veeam staff (Fabian K., 2026-02-11): Alletra MP storage integration features "are only available for VMware backups. They are not supported for Hyper-V backups." — [Veeam forum: HPE Alletra MP integration (Hyper-V)](https://forums.veeam.com/microsoft-hyper-v-f25/hpe-alletra-mp-integration-t102097.html)
- Veeam's December 2025 announcement with HPE cites NVMe support for the B10000 and new snapshot integrations. — [BusinessWire 2025-12-03](https://www.businesswire.com/news/home/20251203727868/en)
- Commvault IntelliSnap supports B10000 (array OS 10.2–10.5; FC and iSCSI). Since Innovation Release 11.42, a "REST API-based HPE Alletra Storage MP Snap engine" supports immutable snapshots and revert for volumes in virtual volume sets or host sets. It requires HTTPS 5989 / HTTP 5988 open from CommServe and MediaAgent to the array. — [Commvault system requirements](https://docs.commvault.com/11.42/software/system_requirements_for_hpe_alletra_storage_mp_primera_hpe_alletra_9000_series_hpe_alletra_storage_mp_b10000_storage_array.html); [Commvault considerations](https://documentation.commvault.com/11.42/commcell-console/considerations_for_hpe_alletra_storage_mp_primera_hpe_alletra_9000_series_hpe_alletra_storage_mp_b10000_storage_array.html)
- HPE–Commvault (June 2025): Commvault Cloud can coordinate simultaneous snapshots and local backups across two synchronously replicated B10000 arrays. — [BusinessWire 2025-06-24](https://www.businesswire.com/news/home/20250624065119/en/HPE-and-Commvault-Strengthen-Strategic-Partnership-to-Deliver-Industry-Leading-Cyber-Resilience-Solutions)
- NAKIVO supports backup from snapshots of HPE 3PAR, Primera, Alletra 9000 and Alletra Storage MP B10000 (and Nimble/Alletra 5000/6000) for "VMware vSphere VMs hosted on NetApp and HPE devices". — [NAKIVO: Backup from Storage Snapshots](https://nakivo.com/backup-from-storage-snapshots)
- Cohesity NetBackup Snapshot Manager documents plug-ins for HPE Alletra 9000 and Alletra 6000 SAN; I saw no B10000 page in results. — [Cohesity NetBackup 11.1: HPE Alletra 9000 SAN array](https://docs.cohesity.com/docs/netbackup/11.1/155729295-170603027-0/v157326497-170603027)
- HPE Data Services Cloud Console (DSCC) has a cloud REST API with B10000-class ("device-type4") volume snapshot endpoints, for example `GET /api/v1/storage-systems/device-type4/{systemId}/volumes/{volumeId}/snapshots/{snapshotId}` (third-party mirror of DSCC API 1.4.0). This is an alternative to on-array WSAPI, but it requires the GreenLake cloud. — [DSCC API mirror (qodex)](https://developers.qodex.ai/hpe-storage-apis/data-services-cloud-console-api-1-4-0/api-v1-storage-systems-device-type4-systemid-volumes-volumeid-snapshots-snapshotid)
- HPE Recovery Manager Central (RMC): an aggregator lists RMC as EOL on 2025-09-16, with RMC 6.3.13 supported until 2027-11-14. This is unverified against HPE, and I found no source showing RMC support for B10000. — [eosl.date: HPE RMC](https://eosl.date/backup/vendor/hpe/hpe-recovery-manager-central/)
- B10000 OS 10.6.0 has been released (six-node scale-out, agentic support automation; reportedly protects immutable snapshot schedules against modification). Seen as a headline and snippet only; the page returned 403. — [StorageReview](https://www.storagereview.com/fr/news/hpe-alletra-storage-mp-b10000-10-6-0-arrives-with-six-node-scale-out-and-agentic-support-automation)
- XCP-ng: Storware supports agentless XCP-ng backup via XAPI and NBD, with no array-snapshot integration mentioned. No Veeam, NAKIVO, Commvault or HPE source covers B10000 snapshots with XenServer or XCP-ng. — [Storware XCP-ng solution brief](https://storware.eu/wp-content/uploads/2023/04/XCP-ng-Backup-and-Recovery-solution-brief.pdf)

### Inferences
- Commvault's ports 5988/5989 are the classic CIM/SMI-S ports. This suggests its older HPE snap engine used SMI-S, while the newer "Alletra Storage MP Snap engine" is REST/WSAPI-based. Our product should use WSAPI v3 on 443 only.
- Every third-party B10000 snapshot integration found is VMware-centric (or Veeam Agent for physical hosts). For XCP-ng LVM SRs no vendor integration exists, so there is no reference behaviour to copy. The XCP-ng-side parsing (LVM + VHD) is entirely our own work.

### Gaps
- HPE GreenLake Backup and Recovery: no source confirms whether it orchestrates B10000 array snapshots (VMware or otherwise).
- HPE's own B10000 "Integration" collateral (a50014477enw) and the B10000 + Veeam/Commvault solution briefs on hpe.com could not be fetched.
- Cohesity/NetBackup B10000 support is unconfirmed.

## 6. Open-source references that call the WSAPI (licences)

### Takeaway
The best public reference for the WSAPI v3 flow is HPE's own **hpe-storage-flowkit-py**: Apache-2.0, v1.0.1, for Alletra MP OS 10.5+, with v1 and v3 clients. OpenStack Cinder's new Alletra MP driver and HPE's new Ansible modules are built on it. **python-3parclient** (Apache-2.0, still updated in Oct 2026) documents the older WSAPI v1 snapshot, volume-set and VLUN calls. All of these are public source, not commercial binaries, so they are compatible with the project policy as API-flow references.

### Cited Findings
- hpe-storage-flowkit-py: "Python client library for interacting with HPE Alletra MP storage arrays". Author Hewlett Packard Enterprise; License Apache-2.0; version 1.0.1 (releases 0.5 to 1.0.1); requires "HPE Alletra MP OS: Version 10.5 or later" and the WSAPI service enabled. Contains `v3/workflows/` (snapshot, volumeset/applicationsets, host, hostset, vlun, task, clone, remote_copy, schedule, user, …) and `v1/`. — [PyPI](https://pypi.org/project/hpe-storage-flowkit-py/); [GitHub hpe-storage/hpe_storage_flowkit_py](https://github.com/hpe-storage/hpe_storage_flowkit_py)
- It is also being packaged for Debian (python-hpe-storage-flowkit-py 0.9.4-1, Apache-2.0). — [Debian NEW queue review](https://dfsg-new-queue.debian.org/reviews/python-hpe-storage-flowkit-py)
- OpenStack Cinder 2026.2 HPE Alletra MP driver (`cinder.volume.drivers.hpe.alletramp_driver`; classes `HPEAlletraMPFCDriver`, `HPEAlletraMPISCSIDriver`, `HPEAlletraMPNVMETCPDriver`). It requires `hpe-storage-flowkit-py`, OS 10.5.0+, and both `/api/v1` and `/api/v3` URLs. — [OpenStack Cinder docs](https://docs.openstack.org/cinder/2026.2/configuration/block-storage/drivers/hpe-alletramp-driver.html)
- python-3parclient: GitHub repo description "a python client that talks to the HPE Alletra MP, HPE Alletra 9000 and HPE Primera". Apache-2.0, last push 2026-10-30, WSAPI v1 (`createSnapshot`, `createSnapshotOfVolumeSet`, VLUN, host and host set calls). — [GitHub hpe-storage/python-3parclient](https://github.com/hpe-storage/python-3parclient)
- hpe_storage_ansible_modules: "HPE Storage Ansible Module" for "HPE Alletra MP (OS version 10.5.x)". It uses FlowKit over WSAPI and includes an `alletramp_snapshot` module plus volume, volume set, host, host set, VLUN and clone modules. The GitHub API reports no detected licence file (last push 2026-11-05). — [GitHub hpe-storage/hpe_storage_ansible_modules](https://github.com/hpe-storage/hpe_storage_ansible_modules)
- Other hpe-storage repos:
  - csi-driver (Apache-2.0, active 2026-11-05)
  - array-exporter ("documentation and binary image releases"; no licence)
  - hpe-cinder-drivers (Apache-2.0, historical)
  - nimble-ansible-modules (GPL-3.0; Nimble/Alletra 5000/6000, not B10000)
  
  — [GitHub org hpe-storage (API listing)](https://github.com/hpe-storage); [HPE Developer: HPE Alletra platform](https://developer.hpe.com/platform/hpe-alletra/home/)
- OpenStack Manila has an "HPE Alletra MP B10000 Share Driver" (file services; not relevant for block). — [OpenStack Manila 2026.1](https://docs.openstack.org/manila/2026.1/configuration/shared-file-systems/drivers/hpe-alletra-mp-b10000-driver.html)

### Inferences
- For a C# implementation, FlowKit v3 is the most precise public map of the endpoints, action names, payload keys, the `experimentalfilter` header and task polling. The OpenAPI spec from developer.hpe.com should still be the normative source for generated DTOs.
- Apache-2.0 code is one-way compatible with (A)GPLv3. Even so, re-implementing from the documented API rather than copying code avoids attribution and NOTICE obligations. This is a licence-compatibility inference, not legal advice.
- The Ansible modules have no detected licence, so treat them as read-only documentation and do not copy from them.

### Gaps
- I did not verify whether the HPE CSI driver's B10000 backend (the CSP for Primera/3PAR/Alletra MP) is open source or a closed container image.
- I did not verify the GitHub file paths inside hpe_storage_flowkit_py. The package imports suggest a `v3/src/...` layout in the repo, while the PyPI sdist uses `v3/workflows/...`.
