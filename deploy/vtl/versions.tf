terraform {
  required_version = ">= 1.8.0"

  required_providers {
    xenorchestra = {
      source  = "vatesfr/xenorchestra"
      version = "0.41.2"
    }
    random = {
      source  = "hashicorp/random"
      version = "3.6.3"
    }
  }
}

# The Xen Orchestra URL and credentials come from XOA_URL and XOA_TOKEN (or XOA_USER/XOA_PASSWORD), see README.md,
# never from a file in the repository.
provider "xenorchestra" {}
