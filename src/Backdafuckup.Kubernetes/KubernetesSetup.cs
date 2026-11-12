namespace Backdafuckup.Kubernetes;

/// <summary>The objects a cluster needs for backdafuckup: its work namespace, a ServiceAccount with a token, and the roles.</summary>
public static class KubernetesSetup
{
    public const string WorkNamespace = "backdafuckup-system";

    public const string ServiceAccount = "backdafuckup";

    public const string TokenSecret = "backdafuckup-token";

    public const string FileName = "backdafuckup-setup.yaml";

    /// <summary>
    /// The resources the product reads by name (<c>get</c>), per API group. Everything else is read by listing: a <c>get</c>
    /// on <c>*/*</c> would also grant the proxy subresources (<c>nodes/proxy</c> reaches the kubelet, and through it exec in
    /// any pod), which a backup never needs. Objects of other kinds are read by name through a list with a field selector
    /// (<see cref="KubernetesApplier.ReadAsync"/>).
    /// </summary>
    public static IReadOnlyList<(string Group, IReadOnlyList<string> Resources)> ReadByName { get; } =
    [
        ("", ["configmaps", "limitranges", "namespaces", "persistentvolumeclaims", "persistentvolumes", "pods", "pods/log", "podtemplates",
              "replicationcontrollers", "resourcequotas", "secrets", "serviceaccounts", "services"]),
        ("apps", ["daemonsets", "deployments", "replicasets", "statefulsets"]),
        ("apiextensions.k8s.io", ["customresourcedefinitions"]),
        ("snapshot.storage.k8s.io", ["volumesnapshotclasses", "volumesnapshotcontents", "volumesnapshots"]),
        ("storage.k8s.io", ["storageclasses"]),
    ];

    private static string ReadByNameRules() => string.Concat(ReadByName.Select(r => $$"""
          - apiGroups: [{{Quote(r.Group)}}]
            resources: [{{string.Join(", ", r.Resources)}}]
            verbs: [get]

        """));

    /// <summary>
    /// The manifest to apply (kubectl or a Fleet bundle). Backup reads every object, Secrets included, takes and removes
    /// volume snapshots, runs hooks in pods and works with PVCs, PVs and pods in the work namespace. Restore creates and
    /// changes objects in any namespace; without it the account only backs up.
    /// </summary>
    public static string Manifest(bool includeRestore = true, ClusterAgentManifest? agent = null) =>
        (includeRestore ? Backup + Restore : Backup) + (agent is null ? string.Empty : Agent(agent));

    public const string AgentName = "backdafuckup-agent";

    /// <summary>
    /// The agent of a cluster the server reaches through it: a Secret with the connect token, the pull secret when the
    /// server creates it, and a Deployment of the mover image in agent mode, as a non-root user without capabilities. It
    /// uses the ServiceAccount of the manifest, whose token it sends to the server.
    /// </summary>
    private static string Agent(ClusterAgentManifest agent)
    {
        var pull = agent.PullSecretName is { } name
            ? $$"""
                    imagePullSecrets:
                      - name: {{Quote(name)}}

              """
            : string.Empty;
        var pullSecret = agent.PullSecretName is { } secretName && agent.DockerConfigJson is { } json
            ? $$"""
              ---
              apiVersion: v1
              kind: Secret
              metadata:
                name: {{Quote(secretName)}}
                namespace: {{WorkNamespace}}
                labels:
                  app.kubernetes.io/managed-by: backdafuckup
              type: kubernetes.io/dockerconfigjson
              data:
                .dockerconfigjson: {{Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(json))}}

              """
            : string.Empty;
        return $$"""
            ---
            # The agent: for a cluster the backup server cannot reach. It dials out to {{agent.ServerUrl}} and carries the server's
            # connections to the API server; TLS runs from the server to the API server through it. The connect token below
            # lets it in: keep this manifest like a password.
            apiVersion: v1
            kind: Secret
            metadata:
              name: {{AgentName}}
              namespace: {{WorkNamespace}}
              labels:
                app.kubernetes.io/managed-by: backdafuckup
            type: Opaque
            stringData:
              token: {{Quote(agent.Token)}}

            """ + pullSecret + $$"""
            ---
            apiVersion: apps/v1
            kind: Deployment
            metadata:
              name: {{AgentName}}
              namespace: {{WorkNamespace}}
              labels:
                app.kubernetes.io/name: {{AgentName}}
                app.kubernetes.io/managed-by: backdafuckup
            spec:
              replicas: 1
              strategy:
                type: Recreate
              selector:
                matchLabels:
                  app.kubernetes.io/name: {{AgentName}}
              template:
                metadata:
                  labels:
                    app.kubernetes.io/name: {{AgentName}}
                spec:
                  serviceAccountName: {{ServiceAccount}}

            """ + pull + $$"""
                  securityContext:
                    runAsNonRoot: true
                    runAsUser: 1654
                    runAsGroup: 1654
                    seccompProfile:
                      type: RuntimeDefault
                  containers:
                    - name: agent
                      image: {{Quote(agent.Image)}}
                      imagePullPolicy: IfNotPresent
                      env:
                        - name: BDFU_MOVER_MODE
                          value: agent
                        - name: BDFU_AGENT_SERVER_URL
                          value: {{Quote(agent.ServerUrl)}}
                        - name: BDFU_AGENT_CONNECTION
                          value: {{Quote(agent.ConnectionId.ToString("D"))}}
                        - name: BDFU_MOVER_SERVER_PINS
                          value: {{Quote(string.Join(',', agent.ServerPins))}}
                        - name: BDFU_AGENT_TOKEN
                          valueFrom:
                            secretKeyRef:
                              name: {{AgentName}}
                              key: token
                        - name: POD_NAME
                          valueFrom:
                            fieldRef:
                              fieldPath: metadata.name
                        - name: NODE_NAME
                          valueFrom:
                            fieldRef:
                              fieldPath: spec.nodeName
                        # The root file system is read-only: no diagnostics socket in /tmp.
                        - name: DOTNET_EnableDiagnostics
                          value: "0"
                      resources:
                        requests:
                          cpu: 50m
                          memory: 64Mi
                        limits:
                          memory: 256Mi
                      securityContext:
                        allowPrivilegeEscalation: false
                        readOnlyRootFilesystem: true
                        capabilities:
                          drop: [ALL]

            """;
    }

    /// <summary>A YAML double-quoted scalar (JSON's string syntax is valid YAML).</summary>
    private static string Quote(string value) => System.Text.Json.JsonSerializer.Serialize(value);

    private static readonly string Backup = $$"""
        # backdafuckup: access to this cluster for backing up and restoring namespaces.
        # Apply it (kubectl apply -f, or as a Fleet bundle), then read the token and the cluster CA for the console:
        #   kubectl -n {{WorkNamespace}} get secret {{TokenSecret}} -o jsonpath='{.data.token}' | base64 -d
        #   kubectl -n {{WorkNamespace}} get secret {{TokenSecret}} -o jsonpath='{.data.ca\.crt}' | base64 -d
        apiVersion: v1
        kind: Namespace
        metadata:
          name: {{WorkNamespace}}
          labels:
            app.kubernetes.io/managed-by: backdafuckup
            # The mover pods run as root (block devices belong to root), without privileges or capabilities: "baseline".
            pod-security.kubernetes.io/enforce: baseline
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
        # volumes to their claims (PVs are cluster-scoped). Objects are read by listing; "get" only on the resources below,
        # never on */*, which would include nodes/proxy (the kubelet's API, and through it exec in any pod).
        apiVersion: rbac.authorization.k8s.io/v1
        kind: ClusterRole
        metadata:
          name: backdafuckup-backup
        rules:
          - apiGroups: ["*"]
            resources: ["*"]
            verbs: [list, watch]
        {{ReadByNameRules()}}  - apiGroups: [snapshot.storage.k8s.io]
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
        # The work namespace: the temporary PVCs from snapshots, the mover pods that read and write them, the pull secret
        # for the mover image when the registry needs a login, and the image of the agent (when the cluster has one), which
        # the server keeps at its own version.
        apiVersion: rbac.authorization.k8s.io/v1
        kind: Role
        metadata:
          name: backdafuckup-work
          namespace: {{WorkNamespace}}
        rules:
          - apiGroups: [""]
            resources: [persistentvolumeclaims, pods, secrets]
            verbs: [create, delete, patch, update]
          - apiGroups: [apps]
            resources: [deployments]
            resourceNames: [{{AgentName}}]
            verbs: [patch]
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
          # Roles and bindings with rights this account does not have itself: RoleBindings to admin or edit, and the
          # ClusterRoles and ClusterRoleBindings of a whole-cluster restore. Together with the rule above this is as
          # much as cluster-admin.
          - apiGroups: ["rbac.authorization.k8s.io"]
            resources: ["roles", "clusterroles"]
            verbs: [bind, escalate]
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

/// <summary>What the agent part of a cluster's setup manifest needs.</summary>
/// <param name="ServerUrl">Where the agent reaches the backup server (Guest agents → Settings).</param>
/// <param name="ServerPins">SHA-256 SPKI pins of the server certificate; empty: the system's CA store.</param>
/// <param name="Token">The cluster's connect token.</param>
/// <param name="Image">The mover image by digest.</param>
public sealed record ClusterAgentManifest(
    Guid ConnectionId, string ServerUrl, IReadOnlyList<string> ServerPins, string Token, string Image, string? PullSecretName, string? DockerConfigJson);
