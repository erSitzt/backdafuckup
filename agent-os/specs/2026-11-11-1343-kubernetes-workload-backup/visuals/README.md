# Visuals

`mockups/` holds the UI mockups of task 2 (2026-11-11). Each is one artboard as a Design Component (`.dc.html`, plain HTML with inline styles). `canvas.json` lays the artboards out.

**Variant A, like the VM pages (chosen):**
- `Main.dc.html`: Kubernetes Clusters, and a cluster's volumes with how their data is backed up.
- `A2-JobEditor.dc.html`: the job editor's Namespaces step.
- `A3-Backups.dc.html`: Backups & Restore with namespaces and the restore menu.
- `A4-Restore.dc.html`: the restore wizard's Objects step, comparing with the live namespace.

**Variant B, an applications view per cluster (kept as an option for parts later):**
- `B1-Applications.dc.html`: applications per cluster with their protection status.
- `B2-Policy.dc.html`: a policy that selects namespaces by name, wildcard or label.
- `B3-Application.dc.html`: an application's restore points, workloads and volumes.
- `B4-Restore.dc.html`: a restore with a choice per volume and a preview.

Names and addresses are placeholders.
