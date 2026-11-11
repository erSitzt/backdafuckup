namespace Backdafuckup.Kubernetes;

/// <summary>The objects a cluster needs for backdafuckup: its work namespace, a ServiceAccount with a token, and the roles.</summary>
public static class KubernetesSetup
{
    public const string WorkNamespace = "backdafuckup-system";

    public const string ServiceAccount = "backdafuckup";

    public const string TokenSecret = "backdafuckup-token";

    public const string FileName = "backdafuckup-setup.yaml";

    /// <summary>
    /// The manifest to apply (kubectl or a Fleet bundle). Backup reads every object, Secrets included, takes and removes
    /// volume snapshots, runs hooks in pods and works with PVCs, PVs and pods in the work namespace. Restore creates and
    /// changes objects in any namespace; without it the account only backs up.
    /// </summary>
    public static string Manifest(bool includeRestore = true) => includeRestore ? Backup + Restore : Backup;

    private static readonly string Backup = $$"""
        # backdafuckup: access to this cluster for backing up and restoring namespaces.
        # Apply it (kubectl apply -f, or as a Fleet bundle), then read the token for the console:
        #   kubectl -n {{WorkNamespace}} get secret {{TokenSecret}} -o jsonpath='{.data.token}' | base64 -d
        apiVersion: v1
        kind: Namespace
        metadata:
          name: {{WorkNamespace}}
          labels:
            app.kubernetes.io/managed-by: backdafuckup
        ---
        apiVersion: v1
        kind: ServiceAccount
        metadata:
          name: {{ServiceAccount}}
          namespace: {{WorkNamespace}}
        ---
        apiVersion: v1
        kind: Secret
        metadata:
          name: {{TokenSecret}}
          namespace: {{WorkNamespace}}
          annotations:
            kubernetes.io/service-account.name: {{ServiceAccount}}
        type: kubernetes.io/service-account-token
        ---
        # Backup: read every object (Secrets too), take and remove volume snapshots, run hooks in pods, and hand restored
        # volumes to their claims (PVs are cluster-scoped).
        apiVersion: rbac.authorization.k8s.io/v1
        kind: ClusterRole
        metadata:
          name: backdafuckup-backup
        rules:
          - apiGroups: ["*"]
            resources: ["*"]
            verbs: [get, list, watch]
          - apiGroups: [snapshot.storage.k8s.io]
            resources: [volumesnapshots, volumesnapshotcontents]
            verbs: [create, delete, patch, update]
          - apiGroups: [""]
            resources: [pods/exec]
            verbs: [create]
          - apiGroups: [""]
            resources: [persistentvolumes]
            verbs: [create, delete, patch, update]
        ---
        apiVersion: rbac.authorization.k8s.io/v1
        kind: ClusterRoleBinding
        metadata:
          name: backdafuckup-backup
        roleRef:
          apiGroup: rbac.authorization.k8s.io
          kind: ClusterRole
          name: backdafuckup-backup
        subjects:
          - kind: ServiceAccount
            name: {{ServiceAccount}}
            namespace: {{WorkNamespace}}
        ---
        # The work namespace: the temporary PVCs from snapshots and the mover pods that read and write them.
        apiVersion: rbac.authorization.k8s.io/v1
        kind: Role
        metadata:
          name: backdafuckup-work
          namespace: {{WorkNamespace}}
        rules:
          - apiGroups: [""]
            resources: [persistentvolumeclaims, pods]
            verbs: [create, delete, patch, update]
        ---
        apiVersion: rbac.authorization.k8s.io/v1
        kind: RoleBinding
        metadata:
          name: backdafuckup-work
          namespace: {{WorkNamespace}}
        roleRef:
          apiGroup: rbac.authorization.k8s.io
          kind: Role
          name: backdafuckup-work
        subjects:
          - kind: ServiceAccount
            name: {{ServiceAccount}}
            namespace: {{WorkNamespace}}

        """;

    private static readonly string Restore = $$"""
        ---
        # Restore: create and change objects in any namespace. Leave this part out for an account that only backs up.
        apiVersion: rbac.authorization.k8s.io/v1
        kind: ClusterRole
        metadata:
          name: backdafuckup-restore
        rules:
          - apiGroups: ["*"]
            resources: ["*"]
            verbs: [create, update, patch, delete]
        ---
        apiVersion: rbac.authorization.k8s.io/v1
        kind: ClusterRoleBinding
        metadata:
          name: backdafuckup-restore
        roleRef:
          apiGroup: rbac.authorization.k8s.io
          kind: ClusterRole
          name: backdafuckup-restore
        subjects:
          - kind: ServiceAccount
            name: {{ServiceAccount}}
            namespace: {{WorkNamespace}}

        """;
}
