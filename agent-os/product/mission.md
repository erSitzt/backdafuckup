# Product Mission

## Problem

XenServer / XCP-ng VMs lack a good, affordable, well-integrated backup solution. Existing options are either heavyweight commercial products with costly licensing, or basic exports that aren't application-aware. They also lack reliable restore workflows for workloads like Active Directory and Microsoft SQL Server.

The same need extends to VMware vSphere and Hyper-V, which the product will also protect later on.

## Target Users

IT admins at businesses that run XCP-ng / XenServer (and later vSphere and Hyper-V), especially smaller businesses that want enterprise-grade, application-aware backup without the cost and complexity of the big commercial suites. Many are used to Veeam and should be able to switch without relearning their workflows.

Our own environment is the first reference installation.

## Solution

A lightweight, self-hosted, **open-source** backup product (AGPL-3.0):

- An easy-to-use UI on par with Veeam from day one: familiar look and feel and workflows (backup jobs, restore points, guided restores) so admins can switch with almost no learning curve, kept as simple as possible
- Open source and built on open-source libraries; no code or binaries from commercial backup products
- Business model: paid support subscriptions, a turnkey appliance and tested updates (similar to Vates / Xen Orchestra). The code itself stays free
- Built only on publicly available, documented APIs. No vendor partner programs (e.g. Broadcom TAP), gated SDKs (e.g. VDDK) or undocumented protocols, so no vendor can cut off access
- Hypervisor-agnostic design: XCP-ng / XenServer first, with VMware vSphere and Hyper-V added later on the same core
- Works with the storage smaller businesses usually have (SMB, NFS, S3) as well as iSCSI block storage and tape
- Application-aware (consistent backups of AD and MS SQL Server workloads), not just crash-consistent VM exports
- Backup from storage snapshots, so the live cluster is barely stressed: VM snapshots last only seconds and the backup reads from the array, not the hosts
- No heavyweight licensing or infrastructure to maintain
