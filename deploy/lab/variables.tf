variable "pool" {
  description = "name_label of the XCP-ng pool in Xen Orchestra."
  type        = string
}

variable "lab_sr" {
  description = "name_label of the SR for the lab VMs. It must carry the tag bdfu-lab in Xen Orchestra; an SR without it is not found, so no lab disk can land on another SR."
  type        = string
}

variable "lab_network" {
  description = "name_label of the network for the lab VMs. The VMs get their addresses by DHCP; the backup server must reach them on 22 (Linux) and 445 (Windows), and they must reach it on 9555."
  type        = string
}

variable "name_prefix" {
  description = "Prefix of the VM names."
  type        = string
  default     = "bdfu-lab"
}

variable "lab_password" {
  description = "Password of every test account in the lab VMs (root, alice, bob, tess, nosudo, and Administrator in Windows). Lab only: it ends up in the cloud-init data and the OpenTofu state."
  type        = string
  sensitive   = true

  validation {
    condition     = length(var.lab_password) >= 12
    error_message = "lab_password must have at least 12 characters."
  }
}

variable "ssh_public_keys" {
  description = "SSH public keys for alice and bob, so you can log in without the password."
  type        = list(string)
  default     = []
}

variable "linux_vms" {
  description = <<-EOT
    Linux lab VMs by short name. The template must have cloud-init and boot with DHCP on the lab network.
    data_disks_gib decides the test layout: one disk gets a GPT partition with XFS (/srv/bdfu-xfs), two or more
    become one LVM volume group with a linear XFS volume across all of them (/srv/bdfu-lvm).
  EOT
  type = map(object({
    template       = string
    cpus           = optional(number, 2)
    memory_gib     = optional(number, 2)
    root_gib       = optional(number, 20)
    data_disks_gib = optional(list(number), [])
  }))
  default = {}
}

variable "windows_vms" {
  description = <<-EOT
    Windows lab VMs by short name. With cloudbase_init = true the template must run cloudbase-init with the NoCloud
    config drive service; it then sets the Administrator password, opens SMB for the agent push installation and
    creates the test data of the lab runbook. Otherwise prepare the VM by hand as the runbook describes.
  EOT
  type = map(object({
    template       = string
    cloudbase_init = optional(bool, true)
    cpus           = optional(number, 2)
    memory_gib     = optional(number, 4)
    disk_gib       = optional(number, 60)
  }))
  default = {}
}
