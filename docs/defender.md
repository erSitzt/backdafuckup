# Microsoft Defender for Endpoint and the guest agent

The **push installation** of the guest agent looks like an attack to endpoint detection: a file copied to an admin share (`C$`/`ADMIN$`), a service created remotely and started as Local System. This page describes what to expect and how to allow backdafuckup narrowly. It follows the approach of the erSitzt/inventory project, which has the same pattern.

Running agents are not affected: they only connect outbound to the backup server over HTTPS.

## What Defender may report

| Report | Cause |
|---|---|
| *Suspicious service creation initiated remotely*, *File dropped and launched from remote location*, *Possible lateral movement* | The push installation itself (copy over SMB, remote service control). |
| *Suspicious file registered as a service* | The service `BackdafuckupAgent` is created on the first installation. |
| A malware verdict ending in `!ml` | No known malware: a machine-learning verdict for an unsigned program few computers have seen. Every new agent build has a new hash. |

## 1. Prefer the setup package

The setup package from *Guest Agents → Download setup*, run as a group policy startup script or through your software distribution, avoids the remote behaviour completely. Once installed, the agent updates itself from the server.

Do not test development builds of the agent on computers onboarded to Defender: every build is a file nobody has seen before.

## 2. Allow the agent files

An allow indicator for the file hashes ends a malware verdict. The hashes change with every agent build. On the backup server:

```bash
sha256sum artifacts/agent/net10/backdafuckup-agent.exe artifacts/agent/*/bdfu-vss.dll
```

In the portal: *Settings → Endpoints → Indicators → File hashes → Add item*, action *Allow*.

**Signed releases:**
- If the agent was built with a release certificate (`scripts/new-signing-cert.sh`, then `scripts/build-agent.sh` with `SIGNING_CERT_PFX`), an indicator of the type *Certificate* replaces the hashes and survives updates.
- Defender only honours certificates the computers trust. Distribute the public `.cer` file to *Trusted Publishers* (and, for a self-signed certificate, *Trusted Root Certification Authorities*) by group policy, and only after you have decided to trust these builds.
- The same distribution stops SmartScreen warnings.

## 3. Tune the behaviour alerts (portal only)

Alert tuning rules cannot be created by API.
1. Open an alert caused by the installation and choose **Tune alert**.
2. Keep the conditions narrow and combine them with AND:
   - the folder `C:\Program Files\backdafuckup Agent\`;
   - the service name `BackdafuckupAgent`;
   - where available, the backup server as the remote device.
3. Use the action *Resolve alert*, so the alerts stay visible.

Do not tune remote service creation in general.

## 4. Check what happened

```kusto
DeviceFileEvents
| where Timestamp > ago(7d)
| where FolderPath startswith @"C:\Program Files\backdafuckup Agent\"
| summarize Files = count(), Computers = dcount(DeviceId) by InitiatingProcessAccountName, RequestSourceIP
```
