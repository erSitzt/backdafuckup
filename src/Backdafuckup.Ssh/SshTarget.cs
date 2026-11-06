using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace Backdafuckup.Ssh;

/// <summary>How to log on to a Linux guest. Password or private key; the sudo password if the account is not root.</summary>
public sealed record SshLogin(string UserName, string? Password, string? PrivateKey, string? Passphrase, string? SudoPassword);

/// <summary>Trust on first use: remembers the key of a new host and rejects a changed one.</summary>
public interface IHostKeyStore
{
    /// <returns>Null if the key is trusted (now or from before); otherwise why it is not.</returns>
    string? Verify(string host, int port, string keyType, string fingerprintSha256);
}

public interface ISshConnector
{
    /// <summary>Connects SSH and SFTP; errors name host, address, port and account.</summary>
    ISshSession Connect(string host, int port, SshLogin login, IHostKeyStore hostKeys, TimeSpan timeout);
}

/// <summary>A logged-on SSH session with SFTP. Not thread-safe.</summary>
public interface ISshSession : IDisposable
{
    /// <summary>For messages: <c>web01 (10.0.0.7):22</c>.</summary>
    string Endpoint { get; }

    string UserName { get; }

    bool IsConnected { get; }

    /// <summary>Logged on as root: the restore script runs without sudo.</summary>
    bool IsRoot { get; }

    /// <summary>Creates <c>/var/tmp/bdfu-&lt;random&gt;</c> (mode 0700) with the restore script in it.</summary>
    string CreateStaging();

    /// <summary>Free bytes for the account in <paramref name="folder"/>, or null if the server does not tell.</summary>
    long? FreeBytes(string folder);

    /// <summary>Uploads a new file; fails if the content is not exactly <paramref name="length"/> bytes.</summary>
    void Upload(string remotePath, Stream content, long length, CancellationToken cancellationToken);

    void WriteText(string remotePath, string text);

    /// <summary>
    /// Runs the staged restore script as root (directly, <c>sudo -n</c>, or <c>sudo -S</c> with the password on stdin;
    /// with a pseudo terminal where sudo requires one). Each output line goes to <paramref name="onLine"/>.
    /// </summary>
    void RunRestoreScript(string stage, IReadOnlyList<string> arguments, Action<string> onLine, CancellationToken cancellationToken);

    /// <summary>Removes what is left of the staging folder (best effort).</summary>
    void RemoveStaging(string stage);
}

public sealed partial class SshConnector : ISshConnector
{
    public ISshSession Connect(string host, int port, SshLogin login, IHostKeyStore hostKeys, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(login);
        ArgumentNullException.ThrowIfNull(hostKeys);
        var ip = Resolve(host);
        var display = ip.ToString() == host ? $"{host}:{port}" : $"{host} ({ip}):{port}";
        var methods = new List<AuthenticationMethod>();
        if (!string.IsNullOrEmpty(login.PrivateKey))
        {
            using var key = new MemoryStream(Encoding.UTF8.GetBytes(login.PrivateKey));
            methods.Add(new PrivateKeyAuthenticationMethod(login.UserName, new PrivateKeyFile(key, login.Passphrase)));
        }

        if (!string.IsNullOrEmpty(login.Password))
        {
            methods.Add(new PasswordAuthenticationMethod(login.UserName, login.Password));
            var interactive = new KeyboardInteractiveAuthenticationMethod(login.UserName);
            interactive.AuthenticationPrompt += (_, e) =>
            {
                foreach (var prompt in e.Prompts)
                {
                    prompt.Response = login.Password;
                }
            };
            methods.Add(interactive);
        }

        if (methods.Count == 0)
        {
            throw new ArgumentException("The Linux credential has neither a password nor a private key.");
        }

        var info = new ConnectionInfo(ip.ToString(), port, login.UserName, [.. methods]) { Timeout = timeout };
        string? rejected = null;
        void Check(object? sender, HostKeyEventArgs e)
        {
            rejected = hostKeys.Verify(host, port, e.HostKeyName, e.FingerPrintSHA256);
            e.CanTrust = rejected is null;
        }

        var ssh = new SshClient(info) { KeepAliveInterval = TimeSpan.FromSeconds(30) };
        var sftp = new SftpClient(info) { KeepAliveInterval = TimeSpan.FromSeconds(30), OperationTimeout = TimeSpan.FromMinutes(5) };
        ssh.HostKeyReceived += Check;
        sftp.HostKeyReceived += Check;
        try
        {
            ssh.Connect();
            sftp.Connect();
        }
        catch (Exception ex)
        {
            ssh.Dispose();
            sftp.Dispose();
            throw ex switch
            {
                _ when rejected is not null => new IOException($"The SSH host key of {display} is not trusted: {rejected}", ex),
                SshAuthenticationException => new UnauthorizedAccessException($"Logon as {login.UserName} at {display} failed: {ex.Message.TrimEnd('.')}.", ex),
                SocketException or SshOperationTimeoutException => new IOException($"No SSH connection to {display}: {ex.Message.TrimEnd('.')}.", ex),
                _ => new IOException($"SSH to {display} failed: {ex.Message.TrimEnd('.')}.", ex),
            };
        }

        var session = new SshSession(ssh, sftp, display, login);
        try
        {
            session.Initialize();
            return session;
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }

    private static IPAddress Resolve(string host)
    {
        if (IPAddress.TryParse(host.Trim('[', ']'), out var ip))
        {
            return ip;
        }

        try
        {
            var addresses = Dns.GetHostAddresses(host);
            return addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork) ?? addresses.FirstOrDefault()
                ?? throw new IOException($"{host} has no address in DNS.");
        }
        catch (SocketException ex)
        {
            throw new IOException($"{host} cannot be resolved by DNS: {ex.Message}.", ex);
        }
    }

    /// <summary>Arguments of the restore script: staging path, keywords and base64 only, never names.</summary>
    [GeneratedRegex("^[A-Za-z0-9+/=._-]+$")]
    internal static partial Regex SafeArgument();

    private sealed class SshSession(SshClient ssh, SftpClient sftp, string endpoint, SshLogin login) : ISshSession
    {
        private const string Marker = "BDFU-$((6*7))";
        private const string MarkerValue = "BDFU-42";

        public string Endpoint => endpoint;

        public string UserName => login.UserName;

        public bool IsConnected => ssh.IsConnected && sftp.IsConnected;

        public bool IsRoot { get; private set; }

        public void Initialize()
        {
            using var id = ssh.RunCommand("id -u");
            IsRoot = id.Result.Trim() == "0";
        }

        public string CreateStaging()
        {
            var stage = $"/var/tmp/bdfu-{Guid.NewGuid():N}";
            sftp.CreateDirectory(stage);
            sftp.ChangePermissions(stage, 700); // SSH.NET takes the octal digits as a decimal number
            using var script = typeof(SshConnector).Assembly.GetManifestResourceStream("Backdafuckup.Ssh.restore.sh")
                ?? throw new InvalidOperationException("The restore script is missing from the program.");
            sftp.UploadFile(script, stage + "/restore.sh", canOverride: false);
            return stage;
        }

        public long? FreeBytes(string folder)
        {
            try
            {
                var status = sftp.GetStatus(folder);
                return (long)Math.Min(long.MaxValue, status.AvailableBlocks * status.FileSystemBlockSize);
            }
            catch (Exception ex) when (ex is SshException or NotSupportedException)
            {
                return null;
            }
        }

        public void Upload(string remotePath, Stream content, long length, CancellationToken cancellationToken)
        {
            // SSH.NET reads the source on this thread, but reports progress on pool threads, also after UploadFile returned:
            // a count taken there can miss the last writes, and an exception there would end the process. So cancellation
            // goes through the source, and the size is what the server has.
            using (var source = new CancellableReadStream(content, cancellationToken))
            {
                sftp.UploadFile(source, remotePath, canOverride: false);
            }

            var size = sftp.GetAttributes(remotePath).Size;
            if (size != length)
            {
                throw new IOException($"{size:N0} bytes arrived at {endpoint} in {remotePath} instead of {length:N0}.");
            }
        }

        public void WriteText(string remotePath, string text)
        {
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(text));
            sftp.UploadFile(stream, remotePath, canOverride: true);
        }

        public void RunRestoreScript(string stage, IReadOnlyList<string> arguments, Action<string> onLine, CancellationToken cancellationToken)
        {
            if (!SafeArgument().IsMatch(stage) || !stage.StartsWith("/var/tmp/bdfu-", StringComparison.Ordinal))
            {
                throw new ArgumentException("Unexpected staging path.", nameof(stage));
            }

            foreach (var argument in arguments)
            {
                if (!SafeArgument().IsMatch(argument))
                {
                    throw new ArgumentException("A script argument contains characters that are not allowed.", nameof(arguments));
                }
            }

            var script = $"sh {stage}/restore.sh {stage} {string.Join(' ', arguments)}";
            if (IsRoot)
            {
                RunCommand(script, null, onLine, cancellationToken);
                return;
            }

            var sudo = login.SudoPassword is { Length: > 0 } ? $"sudo -S -p '' -- {script}" : $"sudo -n -- {script}";
            var (exit, error) = RunCommand(sudo, login.SudoPassword, onLine, cancellationToken);
            if (exit == 0)
            {
                return;
            }

            if (error.Contains("tty", StringComparison.OrdinalIgnoreCase))
            {
                // "sorry, you must have a tty to run sudo" (Defaults requiretty): again with a pseudo terminal.
                RunInTerminal(script, onLine, cancellationToken);
                return;
            }

            var reason = error.Contains("password is required", StringComparison.OrdinalIgnoreCase) || error.Contains("a terminal is required", StringComparison.OrdinalIgnoreCase)
                ? "sudo asks for a password; add the sudo password to the Linux credential"
                : error.Contains("incorrect password", StringComparison.OrdinalIgnoreCase) || error.Contains("Sorry, try again", StringComparison.OrdinalIgnoreCase)
                    ? "the sudo password is wrong"
                    : error.Contains("not in the sudoers", StringComparison.OrdinalIgnoreCase) || error.Contains("not allowed to execute", StringComparison.OrdinalIgnoreCase)
                        ? $"{login.UserName} may not use sudo there"
                        : error.Trim();
            throw new IOException($"Running the restore as root on {endpoint} as {login.UserName} failed (exit code {exit}): {reason}.");
        }

        public void RemoveStaging(string stage)
        {
            if (!stage.StartsWith("/var/tmp/bdfu-", StringComparison.Ordinal) || !sftp.IsConnected)
            {
                return;
            }

            try
            {
                if (!sftp.Exists(stage))
                {
                    return;
                }

                foreach (var file in sftp.ListDirectory(stage).Where(f => f.Name is not ("." or "..")))
                {
                    sftp.DeleteFile(file.FullName);
                }

                sftp.DeleteDirectory(stage);
            }
            catch (Exception ex) when (ex is SshException or IOException)
            {
                // Left behind in /var/tmp; named bdfu-<random>.
            }
        }

        public void Dispose()
        {
            sftp.Dispose();
            ssh.Dispose();
        }

        private (int Exit, string Error) RunCommand(string commandText, string? input, Action<string> onLine, CancellationToken cancellationToken)
        {
            using var command = ssh.CreateCommand(commandText);
            command.CommandTimeout = Timeout.InfiniteTimeSpan;
            var running = command.BeginExecute();
            if (input is not null)
            {
                using var stdin = command.CreateInputStream();
                var bytes = Encoding.UTF8.GetBytes(input + "\n");
                stdin.Write(bytes);
            }

            using (cancellationToken.Register(() => command.CancelAsync()))
            using (var reader = new StreamReader(command.OutputStream, Encoding.UTF8))
            {
                string? line;
                while ((line = reader.ReadLine()) is not null)
                {
                    onLine(line);
                }
            }

            command.EndExecute(running);
            cancellationToken.ThrowIfCancellationRequested();
            return (command.ExitStatus ?? -1, command.Error ?? string.Empty);
        }

        /// <summary>sudo with a pseudo terminal; prompt and exit code come with markers the echoed command line cannot contain.</summary>
        private void RunInTerminal(string script, Action<string> onLine, CancellationToken cancellationToken)
        {
            using var shell = ssh.CreateShellStream("dumb", 500, 50, 0, 0, 1 << 16);
            // Double quotes: the shell computes the markers, so the echoed command line never contains them.
            shell.WriteLine($"sudo -S -p \"{Marker}-PASSWORD:\" -- {script}; echo \"{Marker}-EXIT:$?\"");
            var exit = -1;
            var buffer = new StringBuilder();
            var lastOutput = DateTime.UtcNow;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (DateTime.UtcNow - lastOutput > TimeSpan.FromMinutes(10))
                {
                    throw new IOException($"The restore on {endpoint} as {login.UserName} gave no output for 10 minutes; it was stopped.");
                }

                var line = shell.ReadLine(TimeSpan.FromSeconds(5));
                if (line is null)
                {
                    // The password prompt has no line end.
                    var pending = shell.Read();
                    if (pending.Length > 0)
                    {
                        lastOutput = DateTime.UtcNow;
                    }

                    buffer.Append(pending);
                    if (buffer.ToString().Contains($"{MarkerValue}-PASSWORD:", StringComparison.Ordinal))
                    {
                        if (login.SudoPassword is null)
                        {
                            throw new IOException($"sudo on {endpoint} asks for a password; add the sudo password to the Linux credential.");
                        }

                        shell.WriteLine(login.SudoPassword);
                        buffer.Clear();
                    }

                    continue;
                }

                lastOutput = DateTime.UtcNow;
                line = line.TrimEnd('\r');
                var at = line.IndexOf($"{MarkerValue}-EXIT:", StringComparison.Ordinal);
                if (at >= 0)
                {
                    exit = int.TryParse(line[(at + MarkerValue.Length + 6)..].Trim(), out var code) ? code : -1;
                    break;
                }

                if (line.Contains($"{MarkerValue}-PASSWORD:", StringComparison.Ordinal))
                {
                    shell.WriteLine(login.SudoPassword ?? string.Empty);
                    continue;
                }

                onLine(line);
            }

            if (exit != 0)
            {
                throw new IOException($"Running the restore as root on {endpoint} as {login.UserName} with a terminal failed (exit code {exit}); check the sudo rights and password.");
            }
        }
    }
}

/// <summary>Reads another stream (left open) and throws once the token is cancelled.</summary>
internal sealed class CancellableReadStream(Stream inner, CancellationToken cancellationToken) : Stream
{
    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return inner.Read(buffer, offset, count);
    }

    public override int Read(Span<byte> buffer)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return inner.Read(buffer);
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
