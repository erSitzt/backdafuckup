# ---- Xen Orchestra / XCP-ng ----

variable "pool" {
  description = "Pool name as Xen Orchestra shows it."
  type        = string
}

variable "lab_sr" {
  description = "Storage repository for the VM's disks. It must carry the tag bdfu-lab, or the run stops before anything is created."
  type        = string
}

variable "lab_network" {
  description = "Network the backup server reaches the VTL on (iSCSI 3260, web UI and API on 80)."
  type        = string
}

variable "template" {
  description = "Ubuntu 24.04 template with cloud-init."
  type        = string
}

variable "resource_set" {
  description = "Name of a Xen Orchestra resource set to create the VM in: needed when the account is no pool administrator. Null: none."
  type        = string
  default     = null
}

# ---- VM ----

variable "vm_name" {
  description = "VM and host name."
  type        = string
  default     = "bdfu-lab-vtl"
}

variable "cpus" {
  type    = number
  default = 2
}

variable "memory_gib" {
  type    = number
  default = 4
}

variable "root_gib" {
  description = "System disk; at least the template's disk size."
  type        = number
  default     = 20
}

variable "tape_gib" {
  description = "Second, empty disk: Holo formats it and keeps the cartridges on it."
  type        = number
  default     = 64
}

variable "ssh_public_keys" {
  description = "Keys for the user ubuntu."
  type        = list(string)
  default     = []
}

variable "timezone" {
  type    = string
  default = "Europe/Berlin"
}

variable "ip_cidr" {
  description = "Static IPv4 with prefix, e.g. 10.26.20.60/24. Null: DHCP."
  type        = string
  default     = null
}

variable "gateway" {
  type    = string
  default = null
}

variable "dns_servers" {
  type    = list(string)
  default = []
}

# ---- Holo ----

variable "holo_version" {
  description = "Holo VTL release (github.com/Holo-VTL/Holo, tag Holo@v<version>)."
  type        = string
  default     = "1.0.7"
}

variable "holo_sha256" {
  description = "SHA-256 of holo-vtl-<version>-linux-x86_64.tar.gz; change it together with holo_version."
  type        = string
  default     = "75bf0fefedf03e54639d8948f432cdc0d99031e666ae9a5946e3f588af83cde0"
}

variable "api_key" {
  description = "Key for the REST API and the web UI. Empty: generated (tofu output -raw api_key)."
  type        = string
  default     = ""
  sensitive   = true
}

# ---- The library created on the first boot ----

variable "library_id" {
  description = "Technical id of the library (lower case, digits, '-'); part of the target IQNs."
  type        = string
  default     = "bdf"

  validation {
    condition     = can(regex("^[a-z0-9][a-z0-9-]{0,30}$", var.library_id))
    error_message = "library_id: lower case letters, digits and '-', at most 31 characters."
  }
}

variable "library_name" {
  type    = string
  default = "backdafuckup-lab"
}

variable "library_vendor" {
  type    = string
  default = "HPE"
}

variable "library_type" {
  description = "Holo changer profile, e.g. hpe-msl6480, hpe-msl3040, hp-msl4048."
  type        = string
  default     = "hpe-msl6480"
}

variable "drive_type" {
  description = "Holo drive profile, e.g. hpe-ultrium-9-scsi (LTO-9), hpe-ultrium-8-scsi (LTO-8)."
  type        = string
  default     = "hpe-ultrium-9-scsi"
}

variable "lto_generation" {
  description = "LTO generation of the cartridges; must suit the drives."
  type        = number
  default     = 9
}

variable "drive_count" {
  description = "Drives in the library (Holo allows at most 4)."
  type        = number
  default     = 2

  validation {
    condition     = var.drive_count >= 1 && var.drive_count <= 4
    error_message = "drive_count must be between 1 and 4."
  }
}

variable "slot_count" {
  type    = number
  default = 24
}

variable "ie_port_count" {
  description = "Mail slots (import/export elements)."
  type        = number
  default     = 4
}

variable "cartridge_count" {
  type    = number
  default = 20
}

variable "cartridge_prefix" {
  description = "1-3 characters; Holo makes barcodes like BDF000L09, BDF001L09, …"
  type        = string
  default     = "BDF"

  validation {
    condition     = can(regex("^[A-Z0-9]{1,3}$", var.cartridge_prefix))
    error_message = "cartridge_prefix: 1-3 upper case letters or digits."
  }
}

variable "cartridge_gib" {
  description = "Capacity of each cartridge. Small, so that the end of a tape and tape changes are quick to test."
  type        = number
  default     = 2
}

variable "initiator_iqns" {
  description = "Initiators allowed to log in (the backup server, the tape agent host). Empty: every initiator."
  type        = list(string)
  default     = []

  validation {
    condition     = alltrue([for i in var.initiator_iqns : can(regex("^(iqn\\.|eui\\.|naa\\.)[a-z0-9.:\\-]+$", i))])
    error_message = "IQNs are lower case and start with iqn., eui. or naa."
  }
}
