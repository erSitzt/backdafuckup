# A virtual tape library for the tape checks of the lab runbook (rows T1-T31): Holo VTL (github.com/Holo-VTL/Holo, MIT)
# in one Ubuntu VM, emulating an HPE MSL6480 with LTO-9 drives by default, published over iSCSI. Everything here
# carries the tag bdfu-lab and lives on the lab SR only. `tofu destroy` removes exactly this VM, nothing else.

locals {
  tag = "bdfu-lab"
  gib = 1024 * 1024 * 1024

  api_key = var.api_key != "" ? var.api_key : random_password.api_key.result
  host_ip = var.ip_cidr == null ? null : split("/", var.ip_cidr)[0]

  # A fixed, locally administered MAC, so that a static network config applies whatever the interface is called.
  mac_hex     = substr(sha1("mac-${var.vm_name}"), 0, 10)
  mac_address = join(":", concat(["02"], [for i in range(5) : substr(local.mac_hex, i * 2, 2)]))

  # Read by holo-bootstrap.py on the VM.
  bootstrap = {
    pool_id   = "tapes"
    pool_name = "Tapes"
    library = {
      libraryId          = var.library_id
      poolId             = "tapes"
      name               = var.library_name
      vendor             = var.library_vendor
      libraryType        = var.library_type
      driveType          = var.drive_type
      driveCount         = var.drive_count
      driveStartAddress  = 256
      slotCount          = var.slot_count
      slotStartAddress   = 1024
      iePortCount        = var.ie_port_count
      ieStartAddress     = 768
      compressionEnabled = false
      dedupEnabled       = false
    }
    cartridge_count = var.cartridge_count
    cartridge = {
      barcodePrefix = var.cartridge_prefix
      poolId        = "tapes"
      libraryId     = var.library_id
      capacityBytes = var.cartridge_gib * local.gib
      ltoGeneration = var.lto_generation
      mediaType     = "LTO${var.lto_generation}"
    }
    initiator_iqns = var.initiator_iqns
  }

  setup_script = templatefile("${path.module}/templates/holo-setup.sh.tftpl", {
    holo_version = var.holo_version
    holo_sha256  = var.holo_sha256
    portal_host  = local.host_ip == null ? "" : local.host_ip
  })

  cloud_config = templatefile("${path.module}/templates/cloud-init.yaml.tftpl", {
    hostname           = var.vm_name
    timezone           = var.timezone
    ssh_public_keys    = var.ssh_public_keys
    api_key_b64        = base64encode(local.api_key)
    bootstrap_json_b64 = base64encode(jsonencode(local.bootstrap))
    bootstrap_py_b64   = base64encode(file("${path.module}/templates/holo-bootstrap.py"))
    setup_script_b64   = base64encode(local.setup_script)
  })

  network_config = var.ip_cidr == null ? null : templatefile("${path.module}/templates/network-config.yaml.tftpl", {
    mac_address = local.mac_address
    ip_cidr     = var.ip_cidr
    gateway     = var.gateway
    dns_servers = var.dns_servers
  })
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

data "xenorchestra_template" "ubuntu" {
  name_label = var.template
  pool_id    = data.xenorchestra_pool.lab.id
}

data "xenorchestra_resource_set" "lab" {
  count = var.resource_set == null ? 0 : 1
  name  = var.resource_set
}

resource "random_password" "api_key" {
  length  = 40
  special = false
}

resource "xenorchestra_vm" "vtl" {
  name_label       = var.vm_name
  name_description = "backdafuckup lab VTL (deploy/vtl): Holo ${var.holo_version}, ${var.library_type} with ${var.drive_count} x ${var.drive_type}. Created and removed by OpenTofu only."
  template         = data.xenorchestra_template.ubuntu.id
  resource_set     = var.resource_set == null ? null : data.xenorchestra_resource_set.lab[0].id
  cpus             = var.cpus
  memory_max       = var.memory_gib * local.gib
  memory_min       = var.memory_gib * local.gib
  auto_poweron     = false
  power_state      = "Running"
  tags             = [local.tag]

  cloud_config         = local.cloud_config
  cloud_network_config = local.network_config

  network {
    network_id  = data.xenorchestra_network.lab.id
    mac_address = local.mac_address
  }

  disk {
    sr_id      = data.xenorchestra_sr.lab.id
    name_label = "${var.vm_name}-root"
    size       = var.root_gib * local.gib
  }

  # Empty: Holo's storage pool for the cartridges.
  disk {
    sr_id      = data.xenorchestra_sr.lab.id
    name_label = "${var.vm_name}-tapes"
    size       = var.tape_gib * local.gib
  }

  timeouts {
    create = "20m"
  }

  lifecycle {
    # cloud-init runs at the first boot only: later changes to the scripts must not replace the VM and its cartridges.
    ignore_changes = [cloud_config, cloud_network_config]

    precondition {
      condition     = var.ip_cidr == null || var.gateway != null
      error_message = "A static address (ip_cidr) needs a gateway."
    }
    precondition {
      condition     = var.cartridge_count <= var.slot_count
      error_message = "cartridge_count must not be larger than slot_count."
    }
    precondition {
      condition     = var.cartridge_count * var.cartridge_gib < var.tape_gib
      error_message = "The cartridges (cartridge_count x cartridge_gib) do not fit on the tape disk (tape_gib)."
    }
  }
}
