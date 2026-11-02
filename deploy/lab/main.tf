# Test VMs for the backdafuckup lab runbook (docs/lab-runbook.md). Everything here carries the tag bdfu-lab and
# lives on the lab SR and network only. `tofu destroy` removes exactly the VMs in this state, nothing else.

locals {
  tag = "bdfu-lab"
  gib = 1024 * 1024 * 1024

  description = "backdafuckup lab VM (deploy/lab). Created and removed by OpenTofu only."

  linux_users = [
    { name = "alice", uid = 1001, sudo = "alice ALL=(ALL) ALL" },
    { name = "bob", uid = 1002, sudo = "bob ALL=(ALL) NOPASSWD: ALL" },
    { name = "tess", uid = 1003, sudo = "Defaults:tess requiretty\ntess ALL=(ALL) ALL" },
    { name = "nosudo", uid = 1004, sudo = "" },
  ]

  linux_cloud_config = {
    for key, vm in var.linux_vms : key => {
      hostname          = "${var.name_prefix}-${key}"
      preserve_hostname = false
      disable_root      = false
      ssh_pwauth        = true
      users = [
        for user in local.linux_users : merge(
          { name = user.name, uid = user.uid, shell = "/bin/bash", lock_passwd = false },
          # cloud-init refuses an empty key list, so the key is left out then.
          contains(["alice", "bob"], user.name) && length(var.ssh_public_keys) > 0 ? { ssh_authorized_keys = var.ssh_public_keys } : {},
        )
      ]
      chpasswd = {
        expire = false
        users  = [for name in concat(["root"], [for user in local.linux_users : user.name]) : { name = name, password = var.lab_password, type = "text" }]
      }
      write_files = [
        {
          path        = "/etc/sudoers.d/90-bdfu-lab"
          permissions = "0440"
          content     = "${join("\n", compact([for user in local.linux_users : user.sudo]))}\n"
        },
        {
          path        = "/etc/ssh/sshd_config.d/10-bdfu-lab.conf"
          permissions = "0644"
          content     = "# backdafuckup lab: root and password logins for the SSH restore checks\nPermitRootLogin yes\nPasswordAuthentication yes\n"
        },
        {
          path        = "/usr/local/sbin/bdfu-lab-disks.sh"
          permissions = "0755"
          content     = file("${path.module}/scripts/bdfu-lab-disks.sh")
        },
        {
          path        = "/usr/local/sbin/bdfu-lab-data.sh"
          permissions = "0755"
          content     = file("${path.module}/scripts/bdfu-lab-data.sh")
        },
      ]
      runcmd = [
        ["sh", "-c", "systemctl restart ssh 2>/dev/null || systemctl restart sshd"],
        ["/usr/local/sbin/bdfu-lab-disks.sh"],
        ["/usr/local/sbin/bdfu-lab-data.sh", "/srv/bdfu-lab", "/srv/bdfu-xfs", "/srv/bdfu-lvm"],
      ]
    }
  }
}

data "xenorchestra_pool" "lab" {
  name_label = var.pool
}

data "xenorchestra_sr" "lab" {
  name_label = var.lab_sr
  pool_id    = data.xenorchestra_pool.lab.id
  tags       = [local.tag]
}

data "xenorchestra_network" "lab" {
  name_label = var.lab_network
  pool_id    = data.xenorchestra_pool.lab.id
}

data "xenorchestra_template" "linux" {
  for_each   = var.linux_vms
  name_label = each.value.template
  pool_id    = data.xenorchestra_pool.lab.id
}

data "xenorchestra_template" "windows" {
  for_each   = var.windows_vms
  name_label = each.value.template
  pool_id    = data.xenorchestra_pool.lab.id
}

resource "xenorchestra_vm" "linux" {
  for_each = var.linux_vms

  name_label       = "${var.name_prefix}-${each.key}"
  name_description = local.description
  template         = data.xenorchestra_template.linux[each.key].id
  cpus             = each.value.cpus
  memory_max       = each.value.memory_gib * local.gib
  memory_min       = each.value.memory_gib * local.gib
  auto_poweron     = false
  power_state      = "Running"
  tags             = [local.tag]

  cloud_config = "#cloud-config\n${yamlencode(local.linux_cloud_config[each.key])}"
  # The config drive holds the lab password; it is removed once the VM has booted.
  destroy_cloud_config_vdi_after_boot = true

  network {
    network_id = data.xenorchestra_network.lab.id
  }

  # The first disk is the template's disk, resized; the others are new, empty data disks.
  disk {
    sr_id      = data.xenorchestra_sr.lab.id
    name_label = "${var.name_prefix}-${each.key}-root"
    size       = each.value.root_gib * local.gib
  }

  dynamic "disk" {
    for_each = each.value.data_disks_gib
    content {
      sr_id      = data.xenorchestra_sr.lab.id
      name_label = "${var.name_prefix}-${each.key}-data${disk.key + 1}"
      size       = disk.value * local.gib
    }
  }

  timeouts {
    create = "20m"
  }
}

resource "xenorchestra_vm" "windows" {
  for_each = var.windows_vms

  name_label       = "${var.name_prefix}-${each.key}"
  name_description = local.description
  template         = data.xenorchestra_template.windows[each.key].id
  cpus             = each.value.cpus
  memory_max       = each.value.memory_gib * local.gib
  memory_min       = each.value.memory_gib * local.gib
  auto_poweron     = false
  power_state      = "Running"
  tags             = [local.tag]

  cloud_config = each.value.cloudbase_init ? templatefile("${path.module}/scripts/bdfu-lab-windows.ps1.tftpl", {
    hostname = substr("${var.name_prefix}-${each.key}", 0, 15)
    password = replace(var.lab_password, "'", "''")
  }) : null
  destroy_cloud_config_vdi_after_boot = each.value.cloudbase_init

  network {
    network_id = data.xenorchestra_network.lab.id
  }

  disk {
    sr_id      = data.xenorchestra_sr.lab.id
    name_label = "${var.name_prefix}-${each.key}-c"
    size       = each.value.disk_gib * local.gib
  }

  timeouts {
    create = "30m"
  }
}
