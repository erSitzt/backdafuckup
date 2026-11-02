terraform {
  required_version = ">= 1.8.0"

  required_providers {
    xenorchestra = {
      source  = "vatesfr/xenorchestra"
      version = "0.41.2"
    }
  }
}

# The Xen Orchestra URL and API token come from XOA_URL and XOA_TOKEN (see README.md), never from a file in the
# repository.
provider "xenorchestra" {}
