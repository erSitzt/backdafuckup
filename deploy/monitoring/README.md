# Monitoring backdafuckup with Prometheus and Grafana

The server serves Prometheus metrics at `https://<server>:9555/metrics`. Prometheus reads them with a **metrics token**, which is an API token that can read `/metrics` and nothing else. The server only answers the scrape and never sends anything out.

This folder contains:

| File | What it is |
|---|---|
| `backdafuckup.rules.yml` | Alert rules as a plain Prometheus rule group (`rule_files:`) |
| `prometheusrule.yaml` | The same rules as a `PrometheusRule` for the Prometheus Operator (kube-prometheus-stack), generated from the rule group |
| `grafana-dashboard.json` | A Grafana dashboard: backups, VM snapshots, disks and transports, pool storage, server |

The metric names and their meaning are listed in the admin guide (`docs/admin-guide.md`, *Monitoring with Prometheus*).

## 1. Create the metrics token

In the console go to *API tokens* → *New token*. Choose *Metrics only (Prometheus reads /metrics)* and let it never expire, or set an expiry you will renew. Copy the token (`bdfu_…`): it is shown only once.

A metrics token gets 403 on every other path, and a token can never create tokens. The metric labels contain the names of VMs, jobs, pools, storages and repositories, so treat the token like any other credential.

## 2. Trust the server's certificate

TLS verification stays on. By default the server uses a self-signed certificate, valid for 5 years. Give Prometheus that certificate as its CA:

```sh
openssl s_client -connect backup01.corp.example:9555 -servername backup01.corp.example </dev/null 2>/dev/null \
  | openssl x509 > backdafuckup-server.pem
openssl x509 -in backdafuckup-server.pem -noout -subject -ext subjectAltName -enddate   # check the name and the expiry
```

The server issues a new certificate shortly before the old one expires, or when the host name changes. Replace the file then. A certificate from your own CA (`Kestrel:Certificates:Default`, see the admin guide) avoids this step: point `ca_file` at that CA instead.

## 3. Scrape configuration

### Prometheus Operator (kube-prometheus-stack on rke2)

Store the token and the certificate as a Secret in the namespace of the `ScrapeConfig`:

```sh
kubectl -n monitoring create secret generic backdafuckup-scrape \
  --from-literal=token='bdfu_…' \
  --from-file=ca.pem=backdafuckup-server.pem
```

```yaml
apiVersion: monitoring.coreos.com/v1alpha1
kind: ScrapeConfig
metadata:
  name: backdafuckup
  namespace: monitoring
  labels:
    release: kube-prometheus-stack   # must match the Prometheus' scrapeConfigSelector
spec:
  jobName: backdafuckup              # the alert rules expect this job name
  scheme: HTTPS
  metricsPath: /metrics
  scrapeInterval: 60s
  scrapeTimeout: 30s
  staticConfigs:
    - targets: ["backup01.corp.example:9555"]
  authorization:
    type: Bearer
    credentials:
      name: backdafuckup-scrape
      key: token
  tlsConfig:
    ca:
      secret:
        name: backdafuckup-scrape
        key: ca.pem
    serverName: backup01.corp.example
```

Check from a pod in the cluster that it reaches the server on TCP 9555. The backup server's firewall rule *backdafuckup 9555* allows the port.

### Plain Prometheus

```yaml
scrape_configs:
  - job_name: backdafuckup
    scheme: https
    metrics_path: /metrics
    scrape_interval: 60s
    scrape_timeout: 30s
    authorization:
      type: Bearer
      credentials_file: /etc/prometheus/secrets/backdafuckup-token   # the bdfu_… token, nothing else
    tls_config:
      ca_file: /etc/prometheus/secrets/backdafuckup-server.pem
    static_configs:
      - targets: ["backup01.corp.example:9555"]
```

A scrape interval of 60 s matches how often the server refreshes its catalog gauges. It reads the pools' storages every 5 minutes (`Metrics:PoolInterval`).

## 4. Alert rules

- **Prometheus Operator:** `kubectl apply -f prometheusrule.yaml`. Its label `release` must match the Prometheus' `ruleSelector`.
- **Plain Prometheus:** add `backdafuckup.rules.yml` under `rule_files:`.

Both contain the same 13 alerts:

| Alert | When |
|---|---|
| BackdafuckupDown | the scrape fails |
| BackdafuckupVmBackupFailed | a VM's last backup attempt failed |
| BackdafuckupVmStale | a VM missed a scheduled backup |
| BackdafuckupVmWithoutRestorePoint | a VM has no restore point after a day in a job |
| BackdafuckupStorageBelowMinimum | a storage has less free space than the snapshot minimum |
| BackdafuckupStorageLow | a storage has less than twice that |
| BackdafuckupTransportFallback | disks were read with the HTTP export instead of NBD |
| BackdafuckupSnapshotLeftOver | one of our snapshots is older than 6 h |
| BackdafuckupCleanupPending | a snapshot cleanup is pending for over 2 h |
| BackdafuckupPoolUnreadable | a pool cannot be read |
| BackdafuckupRepositoryLow | a repository has less than 10 % free |
| BackdafuckupSqlLogBackupLate | a SQL log backup is late |
| BackdafuckupAgentOffline | guest agents are offline |

After changing `backdafuckup.rules.yml`, check it and regenerate the `PrometheusRule`:

```sh
docker run --rm -v "$PWD":/r:ro --entrypoint promtool prom/prometheus:v3.5.0 check rules /r/backdafuckup.rules.yml
python3 - <<'EOF'
src = open("backdafuckup.rules.yml").read().splitlines()
start = next(i for i, l in enumerate(src) if l.startswith("groups:"))
head = open("prometheusrule.yaml").read().split("spec:")[0] + "spec:"
open("prometheusrule.yaml", "w").write(head + "\n" + "\n".join("  " + l if l else l for l in src[start:]) + "\n")
EOF
```

## 5. Dashboard

In Grafana go to *Dashboards* → *New* → *Import*, upload `grafana-dashboard.json` and pick the Prometheus data source. It has three variables:
- data source;
- scrape job;
- pool.

Snapshot times are shown against a red 10 s line, the limit of a VSS freeze. Histograms of nightly events are summed per day, so the curves start after the first backups.
