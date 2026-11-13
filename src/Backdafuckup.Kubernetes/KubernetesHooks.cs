using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Backdafuckup.Core.Operations;
using Backdafuckup.Core.Repository;
using k8s;
using k8s.Models;

namespace Backdafuckup.Kubernetes;

/// <summary>Runs a command in a container of a pod (the cluster's exec); tests replace it.</summary>
public interface IPodExecutor
{
    /// <returns>The exit code and the end of the output (stdout and stderr).</returns>
    Task<(int ExitCode, string Output)> ExecAsync(string ns, string pod, string container, IReadOnlyList<string> command, CancellationToken cancellationToken);

    /// <summary>
    /// Runs a command with <paramref name="input"/> on its standard input (one line; secrets go there, not into the command,
    /// which the API server's audit log keeps).
    /// </summary>
    /// <returns>The exit code and its standard output.</returns>
    Task<(int ExitCode, string Output)> ExecAsync(string ns, string pod, string container, IReadOnlyList<string> command, string input, CancellationToken cancellationToken) =>
        throw new NotSupportedException("This executor sends nothing to standard input.");
}

/// <summary>Exec through the API server (a WebSocket to the pod's container).</summary>
internal sealed class ClientPodExecutor(IKubernetes client) : IPodExecutor
{
    public async Task<(int ExitCode, string Output)> ExecAsync(string ns, string pod, string container, IReadOnlyList<string> command, CancellationToken cancellationToken)
    {
        var output = string.Empty;
        var exitCode = await client.NamespacedPodExecAsync(
            pod,
            ns,
            container,
            command,
            false,
            async (_, stdout, stderr) =>
            {
                using var outReader = new StreamReader(stdout);
                using var errReader = new StreamReader(stderr);
                var texts = await Task.WhenAll(outReader.ReadToEndAsync(cancellationToken), errReader.ReadToEndAsync(cancellationToken)).ConfigureAwait(false);
                output = string.Join('\n', texts.Where(t => t.Length > 0)).Trim();
            },
            cancellationToken).ConfigureAwait(false);
        return (exitCode, output);
    }

    public async Task<(int ExitCode, string Output)> ExecAsync(string ns, string pod, string container, IReadOnlyList<string> command, string input, CancellationToken cancellationToken)
    {
        var output = string.Empty;
        var exitCode = await client.NamespacedPodExecAsync(
            pod,
            ns,
            container,
            command,
            false,
            async (stdin, stdout, stderr) =>
            {
                var line = System.Text.Encoding.UTF8.GetBytes(input.ReplaceLineEndings(" ") + "\n");
                await stdin.WriteAsync(line, cancellationToken).ConfigureAwait(false);
                await stdin.FlushAsync(cancellationToken).ConfigureAwait(false);
                using var outReader = new StreamReader(stdout);
                using var errReader = new StreamReader(stderr);
                var texts = await Task.WhenAll(outReader.ReadToEndAsync(cancellationToken), errReader.ReadToEndAsync(cancellationToken)).ConfigureAwait(false);
                output = texts[0].Trim().Length > 0 ? texts[0].Trim() : texts[1].Trim();
            },
            cancellationToken).ConfigureAwait(false);
        return (exitCode, output);
    }
}

/// <summary>A hook of a pod, from its annotations (the names Velero uses, so existing annotations keep working).</summary>
internal sealed record PodHook(string Phase, string Pod, string Container, IReadOnlyList<string> Command, bool FailOnError, TimeSpan Timeout)
{
    public string CommandText => string.Join(' ', Command);
}

/// <summary>
/// Backup hooks: <c>pre.hook.backup.velero.io/command</c> runs in a pod before the snapshots (e.g. a database writes its
/// data to disk), <c>post.hook.backup.velero.io/command</c> after them. Further annotations per phase: <c>container</c>
/// (default: the pod's first), <c>on-error</c> (<c>Fail</c>, the default, or <c>Continue</c>) and <c>timeout</c>
/// (default 30s). Only running pods run hooks.
/// </summary>
internal static class KubernetesHooks
{
    public const string Pre = "pre";
    public const string Post = "post";

    public static IReadOnlyList<PodHook> Find(IEnumerable<V1Pod> pods, string phase)
    {
        var prefix = $"{phase}.hook.backup.velero.io/";
        var result = new List<PodHook>();
        foreach (var pod in pods.Where(p => p.Status?.Phase == "Running").OrderBy(p => p.Metadata.Name, StringComparer.Ordinal))
        {
            var annotations = pod.Metadata.Annotations;
            if (annotations is null || !annotations.TryGetValue(prefix + "command", out var command) || string.IsNullOrWhiteSpace(command))
            {
                continue;
            }

            var container = annotations.TryGetValue(prefix + "container", out var named) && !string.IsNullOrWhiteSpace(named) ? named : pod.Spec.Containers[0].Name;
            var failOnError = !annotations.TryGetValue(prefix + "on-error", out var onError) || !string.Equals(onError, "Continue", StringComparison.OrdinalIgnoreCase);
            var timeout = annotations.TryGetValue(prefix + "timeout", out var text) && ParseDuration(text) is { } parsed ? parsed : TimeSpan.FromSeconds(30);
            result.Add(new PodHook(phase, pod.Metadata.Name, container, ParseCommand(command), failOnError, timeout));
        }

        return result;
    }

    /// <summary>A JSON array of the command and its arguments, or a single word.</summary>
    internal static IReadOnlyList<string> ParseCommand(string command)
    {
        var trimmed = command.Trim();
        if (trimmed.StartsWith('['))
        {
            try
            {
                return JsonSerializer.Deserialize<string[]>(trimmed) ?? [];
            }
            catch (JsonException)
            {
                // Not an array after all: one word.
            }
        }

        return [trimmed];
    }

    /// <summary>Go durations as Velero takes them: <c>30s</c>, <c>2m</c>, <c>1m30s</c>, <c>1h</c>.</summary>
    internal static TimeSpan? ParseDuration(string text)
    {
        var total = TimeSpan.Zero;
        var number = new StringBuilder();
        foreach (var c in text.Trim())
        {
            if (char.IsAsciiDigit(c) || c == '.')
            {
                number.Append(c);
                continue;
            }

            if (number.Length == 0 || !double.TryParse(number.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            {
                return null;
            }

            total += c switch
            {
                'h' => TimeSpan.FromHours(value),
                'm' => TimeSpan.FromMinutes(value),
                's' => TimeSpan.FromSeconds(value),
                _ => TimeSpan.MinValue,
            };
            if (total < TimeSpan.Zero)
            {
                return null;
            }

            number.Clear();
        }

        return number.Length == 0 && total > TimeSpan.Zero ? total : null;
    }

    /// <summary>Runs the hooks one after another; returns what happened to each. Failures are in the records, not thrown.</summary>
    public static async Task<IReadOnlyList<KubernetesHookRecord>> RunAsync(IPodExecutor executor, string cluster, string ns, IReadOnlyList<PodHook> hooks, IOperationLog log, CancellationToken cancellationToken)
    {
        var records = new List<KubernetesHookRecord>();
        foreach (var hook in hooks)
        {
            var started = Stopwatch.GetTimestamp();
            int? exitCode = null;
            string? error;
            var timedOut = false;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(hook.Timeout);
            try
            {
                var (code, output) = await executor.ExecAsync(ns, hook.Pod, hook.Container, hook.Command, timeout.Token).ConfigureAwait(false);
                exitCode = code;
                error = code == 0 ? null : $"exit code {code}{(output.Length > 0 ? $": {Tail(output)}" : string.Empty)}";
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                error = $"no answer within {hook.Timeout.TotalSeconds:0} s";
                timedOut = true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                error = ex.Message;
            }

            var seconds = Stopwatch.GetElapsedTime(started).TotalSeconds;
            KubernetesMetrics.RecordHook(cluster, hook.Phase, error is null ? "ok" : timedOut ? "timeout" : "failed");
            records.Add(new KubernetesHookRecord(hook.Phase, hook.Pod, hook.Container, hook.CommandText, exitCode, error, Math.Round(seconds, 1)));
            var text = $"{(hook.Phase == Pre ? "Pre" : "Post")}-hook in {ns}/{hook.Pod} ({hook.Container}): {hook.CommandText}";
            if (error is null)
            {
                log.Info($"{text} – done in {seconds.ToString("0.0", CultureInfo.InvariantCulture)} s.");
            }
            else
            {
                log.Warning($"{text} – failed: {error}{(hook.FailOnError && hook.Phase == Pre ? " (on-error: Fail)" : string.Empty)}");
            }
        }

        return records;
    }

    private static string Tail(string output) => output.Length > 300 ? "…" + output[^300..] : output;
}
