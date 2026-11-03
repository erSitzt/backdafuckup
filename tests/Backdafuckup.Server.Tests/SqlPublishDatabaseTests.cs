using Backdafuckup.Agent.Core;
using Backdafuckup.Agent.Protocol;

namespace Backdafuckup.Server.Tests;

/// <summary>The agent's attach and drop of published databases, against the scripted SQL Server.</summary>
public sealed class SqlPublishDatabaseTests
{
    private static readonly SqlAccount Login = new() { Kind = SqlAccountKinds.Sql, UserName = "bdfu", Password = "pw" };

    [Fact]
    public void A_database_with_files_outside_the_publish_folder_is_never_dropped()
    {
        var sql = new ScriptedSqlEnvironment(_ => []);
        sql.AddDatabase("shop", @"D:\SQL\shop.mdf", @"C:\publish\1a2b\L\Log\shop_log.ldf");
        var log = new List<string>();

        var refused = Assert.Throws<InvalidOperationException>(() => SqlPublishDatabase.Drop(sql, "SQLEXPRESS", Login, "shop", @"C:\publish\1a2b", log.Add));

        Assert.Contains(@"is not the published one: its files D:\SQL\shop.mdf lie outside C:\publish\1a2b", refused.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(sql.Statements, s => s.StartsWith("ALTER", StringComparison.Ordinal) || s.StartsWith("DROP", StringComparison.Ordinal));
        Assert.Equal(2, sql.DatabaseFiles("shop").Count);

        // A folder that only starts like the publish folder is outside it, too.
        sql.AddDatabase("other", @"C:\publish\1a2b-x\D\other.mdf");
        Assert.Throws<InvalidOperationException>(() => SqlPublishDatabase.Drop(sql, "SQLEXPRESS", Login, "other", @"C:\publish\1a2b", log.Add));

        // Not attached (any more): nothing to do.
        SqlPublishDatabase.Drop(sql, "SQLEXPRESS", Login, "gone", @"C:\publish\1a2b", log.Add);
        Assert.Contains("database [gone] is not attached to instance SQLEXPRESS (any more).", log);
    }

    [Fact]
    public void Names_and_paths_are_quoted_in_the_attach_and_drop_statements()
    {
        var files = new Dictionary<string, byte[]>();
        var sql = new ScriptedSqlEnvironment(path => files[path]);
        var host = new RecordingHost();
        var folder = Path.Combine(Path.GetTempPath(), "it's");
        var mdf = SqlPublishDatabase.FilePath(Path.Combine(folder, "D"), @"SQL\a'b.mdf");
        files[mdf] = [1];
        var attached = false;
        var request = new PublishSqlDatabase
        {
            Instance = "SQLEXPRESS",
            Account = Login,
            Database = "x]; DROP DATABASE master; --",
            Files = [new PublishSqlFile { Volume = "D", Path = @"SQL\a'b.mdf" }],
        };

        SqlPublishDatabase.Attach(sql, host, request, new Dictionary<string, string> { ["D"] = Path.Combine(folder, "D") }, _ => { }, () => attached = true);

        Assert.True(attached);
        Assert.Equal($"CREATE DATABASE [x]]; DROP DATABASE master; --] ON (FILENAME = N'{mdf.Replace("'", "''", StringComparison.Ordinal)}') FOR ATTACH", sql.Statements[1]);
        Assert.Equal([mdf], sql.DatabaseFiles(request.Database));
        Assert.Equal(@"NT SERVICE\MSSQL$SQLEXPRESS", Assert.Single(host.Grants[mdf]));

        SqlPublishDatabase.Drop(sql, "SQLEXPRESS", Login, request.Database, folder, _ => { });
        Assert.Equal("DROP DATABASE [x]]; DROP DATABASE master; --]", sql.Statements[^1]);
        Assert.Empty(sql.DatabaseFiles(request.Database));
    }

    private sealed class RecordingHost : IPublishHost
    {
        public Dictionary<string, IReadOnlyList<string>> Grants { get; } = [];

        public string WorkFolder => Path.GetTempPath();

        public bool Authorize(System.Net.IPEndPoint initiator, string initiatorName) => false;

        public void Attach(int port, string targetName, Action<string> log) => throw new NotSupportedException();

        public int FindDisk(string serial, Action<string> log) => throw new NotSupportedException();

        public void Online(int disk, Action<string> log) => throw new NotSupportedException();

        public void Mount(int disk, long offset, string folder, Action<string> log) => throw new NotSupportedException();

        public void Unmount(string folder, Action<string> log) => throw new NotSupportedException();

        public void Offline(int disk, Action<string> log) => throw new NotSupportedException();

        public void Detach(int port, string targetName, Action<string> log) => throw new NotSupportedException();

        public void GrantAccess(string path, IReadOnlyList<string> accounts, Action<string> log) => Grants[path] = accounts;
    }
}
