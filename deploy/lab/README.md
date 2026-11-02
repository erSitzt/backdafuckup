# Lab VMs (OpenTofu + Xen Orchestra)

Creates the test VMs for the [lab runbook](../../docs/lab-runbook.md) in an existing pool through Xen Orchestra, with the
[`vatesfr/xenorchestra`](https://registry.terraform.io/providers/vatesfr/xenorchestra/latest) provider (MPL-2.0).

## What it creates

- **Linux VMs** (`linux_vms`) from cloud-init templates. Each one gets:
  - the accounts of the SSH restore checks: `root` (password login allowed), `alice` (sudo with password), `bob` (sudo without password), `tess` (sudo with `requiretty`) and `nosudo`, all with `lab_password`;
  - its data disks set up with the distribution's own mkfs defaults: one data disk becomes a GPT partition with XFS on `/srv/bdfu-xfs`; two or more become one LVM volume group over all of them with a linear XFS volume on `/srv/bdfu-lvm`;
  - the runbook's test data in `/srv/bdfu-lab` (root file system) and on the data disks, with hashes and metadata in `/root/bdfu-lab-*before*.txt`;
  - with SELinux on: `/var/www/html/bdfu-lab` for the label check.
- **Windows VMs** (`windows_vms`), optional. With `cloudbase_init = true` the template must run cloudbase-init with the NoCloud config drive service. The VM then gets the Administrator password, SMB opened for the agent push installation, and the runbook's test data in `C:\bdfu-lab`. Install the backdafuckup agent from the console (*Guest Agents → Install agent*).

Suggested set: Ubuntu 24.04 with one data disk (ext4 root, current XFS defaults), a RHEL-compatible 9 or 10 with two data disks (XFS root, LVM over two disks), Windows Server 2022 and 2025.

## Safety

- Everything carries the tag **`bdfu-lab`**. The VM disks go only to an SR that carries this tag in Xen Orchestra: an SR without it is not found, and the run stops before anything is created.
- `tofu destroy` removes exactly the VMs in this folder's state, nothing else in the pool. Run it only yourself, when you mean it.
- Recommended: an Xen Orchestra user for the token whose resource set allows only the lab SR, the lab network and the templates. Then the token cannot touch anything else.
- The cloud-init data holds `lab_password`. The config drive is removed after the first boot, but the password stays in the OpenTofu state (`terraform.tfstate`), which is never committed. Use a password that is used nowhere else.

## Prerequisites

1. In Xen Orchestra: the lab SR with the tag `bdfu-lab`, a lab network with DHCP, and the templates.
   - The backup server must reach the VMs on 22 (Linux) and 445 (Windows).
   - The VMs must reach the backup server on 9555 (agent).
   - The templates should have the guest tools, so the IP addresses are reported.
2. An API token: Xen Orchestra → *User → Authentication tokens*.
3. OpenTofu 1.8 or later, or Docker.

## Use

```bash
cd deploy/lab
cp lab.tfvars.example lab.auto.tfvars        # fill in pool, SR, network, templates
export XOA_URL=wss://xo.example.local         # the Xen Orchestra address
export XOA_TOKEN=...                          # better: read it from a file, not from the shell history
export TF_VAR_lab_password=...                # or put lab_password into lab.auto.tfvars (not committed)

tofu init
tofu plan                                     # shows exactly which VMs and disks would be created
tofu apply
tofu output vms                               # names, UUIDs, IP addresses
```

Without a local OpenTofu, use the same commands in Docker:

```bash
docker run --rm -it -v "$PWD:/w" -w /w -e XOA_URL -e XOA_TOKEN -e TF_VAR_lab_password \
    ghcr.io/opentofu/opentofu:1.13.1 plan
```

The first boot takes a few minutes: cloud-init sets up the disks and writes the test data. Check it with `sudo cloud-init status --long` and `/var/log/cloud-init-output.log`.

Afterwards add the VMs to a backup job in backdafuckup, run it, and work through the runbook.

To remove the lab VMs: `tofu destroy`, in a run you start yourself. Removing a single VM: `tofu destroy -target='xenorchestra_vm.linux["rocky"]'`.
