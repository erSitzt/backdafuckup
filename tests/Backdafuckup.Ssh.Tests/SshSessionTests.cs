using System.Text;
using Backdafuckup.Testing;

namespace Backdafuckup.Ssh.Tests;

/// <summary>
/// The SSH session and the restore script against a real OpenSSH with sudo (Debian container): root login, sudo with
/// and without password, sudo with requiretty, missing sudo rights, wrong passwords and changed host keys.
/// </summary>
[Trait("Category", "Integration")]
public sealed class SshSessionTests : IAsyncLifetime
{
    private SshTestServer? _server;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private SshTestServer Server => _server!;

    public async ValueTask InitializeAsync() => _server = await SshTestServer.StartAsync(Ct);

    public async ValueTask DisposeAsync()
    {
        if (_server is not null)
        {
            await _server.DisposeAsync();
        }
    }

    [Theory]
    [InlineData("root", null)]
    [InlineData("alice", "alicepw")]
    [InlineData("bob", null)]
    [InlineData("tess", "tesspw")]
    public async Task The_restore_script_runs_as_root_for_every_kind_of_account(string user, string? sudoPassword)
    {
        var keys = new RecordingKeys();
        using var session = new SshConnector().Connect(Server.Host, Server.Port, new SshLogin(user, user + "pw", null, null, sudoPassword), keys, TimeSpan.FromSeconds(20));
        Assert.Equal(user == "root", session.IsRoot);
        Assert.Single(keys.Seen);

        var stage = session.CreateStaging();
        Assert.True(session.FreeBytes(stage) > 0);
        var content = Encoding.UTF8.GetBytes($"restored by {user}\n");
        session.Upload(stage + "/1", new MemoryStream(content), content.Length, Ct);
        var folder = $"/srv/restore-{user}";
        session.WriteText(stage + "/manifest", $"D 1 {B64(folder)}\nF 2 {B64(folder + "/report.txt")} 1 0640 1001 1001 202011081805.12 -\nM 3 {B64(folder)} 0750 1002 1002 202111111111.11 -\n");
        var lines = new List<string>();
        session.RunRestoreScript(stage, ["overwrite", "1", B64("/srv")], lines.Add, Ct);

        Assert.Contains("OK 2", lines);
        Assert.Contains("DONE", lines);
        Assert.Equal($"restored by {user}\n", await Server.ExecAsync(Ct, "cat", folder + "/report.txt"));
        Assert.Equal("alice:alice 640 2020-11-08 18:05:12", (await Server.ExecAsync(Ct, "stat", "-c", "%U:%G %a %y", folder + "/report.txt"))[..35]);
        Assert.Equal("bob:bob 750 2021-11-11 11:11:11", (await Server.ExecAsync(Ct, "stat", "-c", "%U:%G %a %y", folder))[..31]);
        Assert.Equal(string.Empty, await Server.ExecAsync(Ct, "sh", "-c", "ls -A /var/tmp | grep bdfu- || true"));
    }

    [Fact]
    public async Task Missing_sudo_rights_and_wrong_passwords_are_named()
    {
        var keys = new RecordingKeys();
        var connector = new SshConnector();
        var login = Assert.Throws<UnauthorizedAccessException>(() => connector.Connect(Server.Host, Server.Port, new SshLogin("alice", "wrong", null, null, null), keys, TimeSpan.FromSeconds(20)));
        Assert.Contains($"Logon as alice at {Server.Host}", login.Message, StringComparison.Ordinal);
        Assert.Contains($":{Server.Port}", login.Message, StringComparison.Ordinal);

        using (var nosudo = connector.Connect(Server.Host, Server.Port, new SshLogin("nosudo", "nosudopw", null, null, null), keys, TimeSpan.FromSeconds(20)))
        {
            var stage = nosudo.CreateStaging();
            nosudo.WriteText(stage + "/manifest", string.Empty);
            var ex = Assert.Throws<IOException>(() => nosudo.RunRestoreScript(stage, ["overwrite", "1", B64("/srv")], _ => { }, Ct));
            Assert.Contains("sudo asks for a password", ex.Message, StringComparison.Ordinal);
            nosudo.RemoveStaging(stage);
        }

        using (var alice = connector.Connect(Server.Host, Server.Port, new SshLogin("alice", "alicepw", null, null, "wrong"), keys, TimeSpan.FromSeconds(20)))
        {
            var stage = alice.CreateStaging();
            alice.WriteText(stage + "/manifest", string.Empty);
            var ex = Assert.Throws<IOException>(() => alice.RunRestoreScript(stage, ["overwrite", "1", B64("/srv")], _ => { }, Ct));
            Assert.Contains("as alice failed", ex.Message, StringComparison.Ordinal);
            alice.RemoveStaging(stage);
        }

        Assert.Equal(string.Empty, await Server.ExecAsync(Ct, "sh", "-c", "ls -A /var/tmp | grep bdfu- || true"));
    }

    [Fact]
    public void A_changed_host_key_is_refused_with_both_fingerprints()
    {
        var keys = new RecordingKeys { Reject = "trusted since yesterday was ssh-ed25519 OLDFINGERPRINT, now NEWFINGERPRINT" };
        var ex = Assert.Throws<IOException>(() => new SshConnector().Connect(Server.Host, Server.Port, new SshLogin("root", "rootpw", null, null, null), keys, TimeSpan.FromSeconds(20)));
        Assert.Contains("is not trusted: trusted since yesterday was ssh-ed25519 OLDFINGERPRINT", ex.Message, StringComparison.Ordinal);
    }

    private static string B64(string text) => Convert.ToBase64String(Encoding.UTF8.GetBytes(text));

    private sealed class RecordingKeys : IHostKeyStore
    {
        public string? Reject { get; init; }

        public List<string> Seen { get; } = [];

        public string? Verify(string host, int port, string keyType, string fingerprintSha256)
        {
            lock (Seen)
            {
                var key = $"{host}:{port} {keyType} {fingerprintSha256}";
                if (!Seen.Contains(key))
                {
                    Seen.Add(key);
                }
            }

            return Reject;
        }
    }
}
