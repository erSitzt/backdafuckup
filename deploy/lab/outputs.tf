output "vms" {
  description = "The lab VMs with their UUIDs and the IPv4 addresses the guest tools report."
  value = merge(
    { for key, vm in xenorchestra_vm.linux : vm.name_label => { uuid = vm.id, ipv4 = vm.ipv4_addresses } },
    { for key, vm in xenorchestra_vm.windows : vm.name_label => { uuid = vm.id, ipv4 = vm.ipv4_addresses } },
  )
}
