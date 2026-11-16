#!/usr/bin/env bash
# Prints what backdafuckup's platform detection looks at in a Kubernetes cluster, as JSON for a test fixture
# (tests/fixtures/k8s-platforms/): the version, the API groups, the namespaces, a sample of nodes and the CSI drivers.
# Read-only (get and list only). Names that may be internal are replaced: node names, label and annotation values
# (except true/false and a few well-known keys), provider ids after their scheme, and namespaces that do not look like
# a platform's. Check the output before sharing it anyway.
#
# Usage: scripts/k8s-platform-facts.sh [kubectl args, e.g. --kubeconfig x.yml --context y] > facts.json
set -euo pipefail

k() { kubectl "$@" ; }
args=("$@")

version=$(k "${args[@]}" version -o json | jq -r '.serverVersion.gitVersion')
groups=$(k "${args[@]}" get --raw /apis | jq '[.groups[].name] | unique')
namespaces=$(k "${args[@]}" get namespaces -o json | jq '[.items[].metadata.name]')
nodes=$(k "${args[@]}" get nodes --chunk-size=20 -o json | jq '[.items[:20][] | {providerId: .spec.providerID, labels: (.metadata.labels // {}), annotations: (.metadata.annotations // {})}]')
drivers=$(k "${args[@]}" get csidrivers -o json | jq '[.items[].metadata.name]')
provisioners=$(k "${args[@]}" get storageclasses -o json | jq '[.items[].provisioner]')

jq -n \
  --arg version "$version" \
  --argjson groups "$groups" \
  --argjson namespaces "$namespaces" \
  --argjson nodes "$nodes" \
  --argjson drivers "$drivers" \
  --argjson provisioners "$provisioners" '
  # Namespaces that look like a platform keep their names; the others become ns-1, ns-2, ...
  def platformish: test("^(default|local|kube-.*|cattle-.*|fleet-.*|cluster-fleet-.*|openshift.*|calico-.*|tigera-.*|cilium.*|gke-.*|gmp-.*|amazon-.*|aws-.*|azure-.*|aks-.*|app-routing-.*|gatekeeper-.*|tkg-.*|vmware-.*|tanzu-.*|longhorn-.*|local-path-storage|istio-.*|cert-manager|ingress-nginx|backdafuckup-.*)$");
  def keepvalue($key; $value): ($value == "true" or $value == "false" or $value == "")
    or ($key | test("^(kubernetes\\.io/(os|arch)|node\\.kubernetes\\.io/instance-type|node-role\\.kubernetes\\.io/.*)$"));
  {
    gitVersion: $version,
    apiGroups: $groups,
    namespaces: ([$namespaces[] | select(platformish)] + [range(0; [$namespaces[] | select(platformish | not)] | length) | "ns-\(. + 1)"]),
    nodes: [$nodes | to_entries[] | {
      name: "node-\(.key + 1)",
      providerId: (if .value.providerId then (.value.providerId | capture("^(?<s>[a-z0-9]+://)").s) else null end),
      labels: (.value.labels | with_entries(.value = (if keepvalue(.key; .value) then .value else "x" end))),
      annotations: (.value.annotations | with_entries(.value = "x"))
    }],
    csiDrivers: (($drivers + $provisioners) | unique)
  }'
