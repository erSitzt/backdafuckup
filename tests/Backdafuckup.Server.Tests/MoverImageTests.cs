using System.Text.Json;
using Backdafuckup.Server.Movers;

namespace Backdafuckup.Server.Tests;

/// <summary>The mover image archive of the .NET SDK, and pushing it to a registry.</summary>
public sealed class MoverImageTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void The_sdk_archive_becomes_a_docker_v2_manifest_with_its_digest_and_a_unique_tag()
    {
        var bundle = MoverImageBundle.Read(new MemoryStream(FakeRegistry.ImageArchive("0.3.0", "a.dll", "b.dll")), "mover.tar");

        Assert.Equal("0.3.0", bundle.Version);
        Assert.Equal(2, bundle.Layers.Count);
        Assert.All(bundle.Layers, l => Assert.Equal(MoverImageBundle.GzipLayerMediaType, l.MediaType));
        Assert.Matches("^0\\.3\\.0-[0-9a-f]{12}$", bundle.Tag);
        Assert.StartsWith(bundle.ManifestDigest["sha256:".Length..][..12], bundle.Tag[^12..], StringComparison.Ordinal);

        using var manifest = JsonDocument.Parse(bundle.Manifest);
        Assert.Equal(MoverImageBundle.ManifestMediaType, manifest.RootElement.GetProperty("mediaType").GetString());
        Assert.Equal(bundle.Config.Digest, manifest.RootElement.GetProperty("config").GetProperty("digest").GetString());
        Assert.Equal(bundle.Config.Size, manifest.RootElement.GetProperty("config").GetProperty("size").GetInt64());
        Assert.Equal(bundle.Layers.Select(l => l.Digest), manifest.RootElement.GetProperty("layers").EnumerateArray().Select(l => l.GetProperty("digest").GetString()));

        var broken = Assert.Throws<InvalidDataException>(() => MoverImageBundle.Read(new MemoryStream(new byte[1024]), "/opt/bdfu/mover/backdafuckup-mover.tar"));
        Assert.Contains("/opt/bdfu/mover/backdafuckup-mover.tar has no manifest.json", broken.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_push_uploads_missing_blobs_and_the_manifest_with_a_token_for_the_repository()
    {
        var bundle = MoverImageBundle.Read(new MemoryStream(FakeRegistry.ImageArchive("0.3.0", "a.dll", "b.dll")), "mover.tar");
        var registry = new FakeRegistry("bdfu-push", "secret-1");
        using (var client = new RegistryClient(registry, "registry.test:8443", "bdfu-push", "secret-1", MoverRegistry.DefaultUserAgent))
        {
            var lines = new List<string>();
            var digest = await client.PushAsync("backdafuckup/mover", [bundle.Config, .. bundle.Layers], bundle.Manifest, MoverImageBundle.ManifestMediaType, bundle.Tag, lines.Add, Ct);
            Assert.Equal(bundle.ManifestDigest, digest);
            Assert.Equal(3, registry.Blobs.Count);
            Assert.Equal(bundle.Manifest, registry.Manifests[$"backdafuckup/mover:{bundle.Tag}"].Content);
            Assert.Equal(MoverImageBundle.ManifestMediaType, registry.Manifests[$"backdafuckup/mover:{bundle.Tag}"].MediaType);
            Assert.Equal(["registry.test:8443 repository:backdafuckup/mover:pull,push"], registry.Scopes);
            Assert.Equal(4, lines.Count);
        }

        // Again, with the token expired meanwhile: a new token, the blobs are there, only the manifest is written.
        registry.ValidFromToken = 2;
        registry.Requests.Clear();
        using (var client = new RegistryClient(registry, "registry.test:8443", "bdfu-push", "secret-1", MoverRegistry.DefaultUserAgent))
        {
            await client.PushAsync("backdafuckup/mover", [bundle.Config, .. bundle.Layers], bundle.Manifest, MoverImageBundle.ManifestMediaType, bundle.Tag, null, Ct);
        }

        Assert.DoesNotContain(registry.Requests, r => r.StartsWith("POST", StringComparison.Ordinal));
        Assert.Equal(3, registry.Requests.Count(r => r.StartsWith("HEAD", StringComparison.Ordinal)));

        // Basic authentication, as some registries ask for it.
        var basic = new FakeRegistry("bdfu-push", "secret-1", basicOnly: true);
        using (var client = new RegistryClient(basic, "registry.test", "bdfu-push", "secret-1", MoverRegistry.DefaultUserAgent))
        {
            await client.PushAsync("tools/backdafuckup/mover", [bundle.Config, .. bundle.Layers], bundle.Manifest, MoverImageBundle.ManifestMediaType, bundle.Tag, null, Ct);
        }

        Assert.True(basic.Manifests.ContainsKey($"tools/backdafuckup/mover:{bundle.Tag}"));
    }

    [Fact]
    public async Task A_proxy_that_lets_only_docker_clients_through_passes_the_default_user_agent_and_names_a_refused_one()
    {
        var bundle = MoverImageBundle.Read(new MemoryStream(FakeRegistry.ImageArchive("0.3.0", "a.dll")), "mover.tar");
        var registry = new FakeRegistry("bdfu-push", "secret-1") { RequiredUserAgentPrefix = "docker/" };
        using (var client = new RegistryClient(registry, "registry.test:8443", "bdfu-push", "secret-1", MoverRegistry.DefaultUserAgent))
        {
            await client.PushAsync("backdafuckup/mover", [bundle.Config, .. bundle.Layers], bundle.Manifest, MoverImageBundle.ManifestMediaType, bundle.Tag, null, Ct);
        }

        Assert.Matches(@"^docker/[0-9.]+ \(compatible; backdafuckup/[0-9.]+\)$", MoverRegistry.DefaultUserAgent);
        Assert.All(registry.UserAgents, ua => Assert.Equal(MoverRegistry.DefaultUserAgent, ua));
        Assert.Contains(registry.Requests, r => r == "GET /token");

        using (var client = new RegistryClient(registry, "registry.test:8443", "bdfu-push", "secret-1", "backdafuckup/0.3.0"))
        {
            var error = await Assert.ThrowsAsync<RegistryException>(() =>
                client.PushAsync("backdafuckup/mover", [bundle.Config, .. bundle.Layers], bundle.Manifest, MoverImageBundle.ManifestMediaType, bundle.Tag, null, Ct));
            Assert.StartsWith("The registry registry.test:8443 refused checking its API (/v2/): 403 Forbidden", error.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Wrong_credentials_or_none_name_the_registry_and_the_repository()
    {
        var bundle = MoverImageBundle.Read(new MemoryStream(FakeRegistry.ImageArchive("0.3.0", "a.dll")), "mover.tar");
        using (var client = new RegistryClient(new FakeRegistry("bdfu-push", "secret-1"), "registry.test:8443", "bdfu-push", "wrong", MoverRegistry.DefaultUserAgent))
        {
            var error = await Assert.ThrowsAsync<RegistryException>(() =>
                client.PushAsync("backdafuckup/mover", [bundle.Config, .. bundle.Layers], bundle.Manifest, MoverImageBundle.ManifestMediaType, bundle.Tag, null, Ct));
            Assert.StartsWith(
                "The registry registry.test:8443 refused getting a token for backdafuckup/mover from https://registry.test:8443/token (user bdfu-push): 401",
                error.Message,
                StringComparison.Ordinal);
            Assert.EndsWith("The user name or password is wrong.", error.Message, StringComparison.Ordinal);
        }

        using (var client = new RegistryClient(new FakeRegistry("bdfu-push", "secret-1"), "registry.test:8443", null, null, MoverRegistry.DefaultUserAgent))
        {
            var error = await Assert.ThrowsAsync<RegistryException>(() =>
                client.PushAsync("backdafuckup/mover", [bundle.Config, .. bundle.Layers], bundle.Manifest, MoverImageBundle.ManifestMediaType, bundle.Tag, null, Ct));
            Assert.Equal("The registry registry.test:8443 needs credentials for pushing to backdafuckup/mover; none are set.", error.Message);
        }
    }

    [Fact]
    public void The_pull_secret_comes_from_the_pull_user_else_the_push_user_or_is_an_existing_one()
    {
        var pushed = new MoverPush("registry.test:8443", "backdafuckup/mover", "0.3.0-abc", "sha256:abc", "0.3.0", DateTimeOffset.UnixEpoch);
        var config = new MoverRegistryConfig("registry.test:8443", "backdafuckup/mover", "bdfu-push", "push-1", "Create", null, null, null);

        var created = MoverRegistry.Deployment(config, pushed);
        Assert.Equal(("registry.test:8443/backdafuckup/mover@sha256:abc", "backdafuckup-registry"), (created.Image, created.PullSecretName));
        using (var json = JsonDocument.Parse(created.DockerConfigJson!))
        {
            var auth = json.RootElement.GetProperty("auths").GetProperty("registry.test:8443");
            Assert.Equal("bdfu-push", auth.GetProperty("username").GetString());
            Assert.Equal(Convert.ToBase64String("bdfu-push:push-1"u8.ToArray()), auth.GetProperty("auth").GetString());
        }

        var pull = MoverRegistry.Deployment(config with { PullUserName = "bdfu-pull", PullPassword = "pull-1" }, pushed);
        Assert.Contains(Convert.ToBase64String("bdfu-pull:pull-1"u8.ToArray()), pull.DockerConfigJson, StringComparison.Ordinal);

        Assert.Equal((null, null), (MoverRegistry.Deployment(config with { UserName = null }, pushed) is var anonymous ? (anonymous.PullSecretName, anonymous.DockerConfigJson) : default));
        Assert.Equal(("registry-pull", null), (MoverRegistry.Deployment(config with { PullSecret = "Existing", ExistingSecret = "registry-pull" }, pushed) is var existing ? (existing.PullSecretName, existing.DockerConfigJson) : default));
        Assert.Null(MoverRegistry.Deployment(config with { PullSecret = "None" }, pushed).PullSecretName);
    }
}
