using Backdafuckup.Agent.Protocol;
using Backdafuckup.Server.Agents;
using Microsoft.Extensions.DependencyInjection;

namespace Backdafuckup.Server.Tests;

/// <summary>
/// An agent in a simulated Windows guest: enrolls with the server's enrollment secret, polls the registry and answers
/// prepare/freeze/thaw like the real agent with its VSS provider would.
/// </summary>
internal sealed class FakeGuestAgent : IAsyncDisposable
{
    private readonly IServiceProvider _services;
    private readonly string _vmUuid;
    private readonly CancellationTokenSource _stop = new();
    private Task? _loop;

    public FakeGuestAgent(IServiceProvider services, string vmUuid)
    {
        _services = services;
        _vmUuid = vmUuid;
    }

    public Guid AgentId { get; private set; }

    public List<string> Commands { get; } = [];

    /// <summary>Also reports a database that the SQL writer did not include in the VSS backup.</summary>
    public bool IncludeUnselectedDatabase { get; set; }

    /// <summary>The SQL databases to report instead of the default "shop".</summary>
    public List<GuestSqlDatabase>? SqlDatabases { get; set; }

    public bool Frozen { get; private set; }

    /// <summary>Thaw answer: by default consistent; VSS_E_HOLD_WRITES_TIMEOUT simulates a snapshot over 10 s.</summary>
    public int ThawVssResult { get; set; }

    public string? FailCommand { get; set; }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var settings = _services.GetRequiredService<AgentSettingsStore>();
        var agents = _services.GetRequiredService<AgentService>();
        var enrolled = await agents.EnrollAsync(
            new AgentEnrollRequest
            {
                EnrollmentSecret = await settings.GetEnrollmentSecretAsync(cancellationToken),
                Host = new AgentHostInfo
                {
                    HostName = "SQL01",
                    BiosSerial = _vmUuid,
                    Build = AgentProtocol.BuildNet10,
                    AgentVersion = "test",
                    OsName = "Windows Server 2022 Standard",
                    IpAddresses = ["10.0.0.5"],
                    VssProvider = "registered",
                },
            },
            "10.0.0.5",
            cancellationToken) ?? throw new InvalidOperationException("Enrollment failed.");
        AgentId = enrolled.AgentId;
        var registry = _services.GetRequiredService<AgentRegistry>();
        _loop = Task.Run(async () =>
        {
            while (!_stop.IsCancellationRequested)
            {
                var command = await registry.NextCommandAsync(AgentId, TimeSpan.FromSeconds(1), _stop.Token);
                if (command is not null)
                {
                    registry.Complete(AgentId, Answer(command));
                }
            }
        });
    }

    private AgentCommandResult Answer(AgentCommand command)
    {
        lock (Commands)
        {
            Commands.Add(command.Kind);
        }

        if (command.Kind == FailCommand)
        {
            return new AgentCommandResult { CommandId = command.Id, Error = "VSS_E_WRITERERROR_TIMEOUT: a writer did not respond", ErrorCode = unchecked((int)0x80042319) };
        }

        var result = new AgentCommandResult { CommandId = command.Id, Success = true };
        switch (command.Kind)
        {
            case AgentCommandKinds.PrepareBackup:
                result.Prepare = new GuestBackupInfo
                {
                    Volumes = [new GuestVolume { Name = @"\\?\Volume{1}\", MountPoints = [@"C:\"], FileSystem = "NTFS", SizeBytes = 64L << 30 }],
                    Writers = [new GuestWriter { Name = "SqlServerWriter", WriterId = "a65faa63-5ea8-4ebc-9dbd-a0c4db26912a", State = "Stable", Components = [@"SQL01\shop", @"SQL01\model"] }],
                    SqlDatabases =
                    [
                        new GuestSqlDatabase
                        {
                            Instance = "MSSQLSERVER", Server = "SQL01", Database = "shop", Files = [@"D:\SQL\shop.mdf", @"D:\SQL\shop_log.ldf"],
                            DataFiles = [new GuestSqlFile { Path = @"D:\SQL\", Spec = "shop.mdf" }],
                            LogFiles = [new GuestSqlFile { Path = @"D:\SQL\", Spec = "shop_log.ldf" }],
                            InBackup = true,
                        },
                    ],
                    SqlWriterMetadataXml = "<WRITER_METADATA writerId=\"a65faa63\"/>",
                };
                if (SqlDatabases is not null)
                {
                    result.Prepare.SqlDatabases = SqlDatabases;
                }

                if (IncludeUnselectedDatabase)
                {
                    result.Prepare.SqlDatabases.Add(new GuestSqlDatabase
                    {
                        Instance = "MSSQLSERVER", Server = "SQL01", Database = "notselected", Files = [@"D:\SQL\ns.mdf"],
                        DataFiles = [new GuestSqlFile { Path = @"D:\SQL\", Spec = "ns.mdf" }],
                        InBackup = false,
                    });
                }

                break;
            case AgentCommandKinds.Freeze:
                Frozen = true;
                result.Freeze = new FreezeReport { FreezeMs = 400 };
                break;
            case AgentCommandKinds.Thaw:
                Frozen = false;
                result.Thaw = new ThawReport
                {
                    HeldMs = 1500,
                    Consistent = ThawVssResult == 0,
                    VssResult = ThawVssResult,
                    Writers = [new GuestWriter { Name = "SqlServerWriter", WriterId = "a65faa63-5ea8-4ebc-9dbd-a0c4db26912a", State = ThawVssResult == 0 ? "Stable" : "FailedAtFreeze" }],
                    BackupComponentsXml = ThawVssResult == 0 ? "<BACKUP_COMPONENTS/>" : null,
                };
                break;
            case AgentCommandKinds.Abort:
                Frozen = false;
                break;
        }

        return result;
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        if (_loop is not null)
        {
            try
            {
                await _loop;
            }
            catch (OperationCanceledException)
            {
            }
        }

        _stop.Dispose();
    }
}
