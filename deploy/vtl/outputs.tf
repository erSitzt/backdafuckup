locals {
  vm_ip = local.host_ip != null ? local.host_ip : try(xenorchestra_vm.vtl.ipv4_addresses[0], "<address of the VM>")

  # Holo names its targets this way.
  iqn_prefix = "iqn.2026-04.cloud.backupnext.holo"
}

output "vm" {
  value = { name = xenorchestra_vm.vtl.name_label, id = xenorchestra_vm.vtl.id, ip = local.vm_ip }
}

output "web_ui" {
  description = "Plain HTTP: use it in the lab network only."
  value       = "http://${local.vm_ip}/ui/"
}

output "api_url" {
  value = "http://${local.vm_ip}"
}

output "api_key" {
  description = "Login of the web UI and header X-HOLO-API-Key of the REST API."
  value       = local.api_key
  sensitive   = true
}

output "iscsi_portal" {
  value = "${local.vm_ip}:3260"
}

output "iscsi_targets" {
  description = "One target per device: the changer, and one per drive."
  value = concat(
    ["${local.iqn_prefix}:library-${var.library_id}"],
    [for i in range(var.drive_count) : format("%s:drive-%s-drv-%02d", local.iqn_prefix, var.library_id, i + 1)],
  )
}
