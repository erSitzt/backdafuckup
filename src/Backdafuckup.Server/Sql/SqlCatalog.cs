using System.Globalization;
using Backdafuckup.Agent.Protocol;
using Backdafuckup.Contracts;
using Backdafuckup.FileLevel;
using Backdafuckup.FileLevel.Zip;
using Backdafuckup.Server.FileRestore;
using DiscUtils.Registry;
using DiscUtils.Streams;
using FileLevelVolume = Backdafuckup.FileLevel.GuestVolume;

namespace Backdafuckup.Server.Sql;

/// <summary>
/// Describes the SQL Server databases of a restore point opened in a file browser: the agent's guest metadata gives the
/// databases and their files; the backup itself gives the instances (registry), the file sizes and the primary file.
/// Everything read from the backup is untrusted; a damaged hive or file only adds a problem or warning.
/// </summary>
public sealed class SqlCatalog(FileLevelWorkers workers)
{
    private const string SoftwareHive = "Windows/System32/config/SOFTWARE";
    private const string SystemHive = "Windows/System32/config/SYSTEM";
    private const string SqlRoot = @"Microsoft\Microsoft SQL Server";

    private static readonly string[] SystemDatabases = ["master", "model", "msdb", "tempdb"];

    public async Task<SqlMountDto> DescribeAsync(BrowseMount mount, CancellationToken cancellationToken)
    {
        var stored = await StoredGuestMetadata.ReadAsync(mount.Reader, cancellationToken).ConfigureAwait(false);
        return await workers.Run(
            () =>
            {
                lock (mount.Gate)
                {
                    return Describe(mount.Image, stored);
                }
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The download of a database's files: the files as they are in the backup (primary file first), named uniquely,
    /// plus a script that attaches them. Refused if a file is missing or cannot be read.
    /// </summary>
    public async Task<FileDownloadTicketDto> PrepareDownloadAsync(BrowseMount mount, FileRestoreDownloads downloads, SqlDownloadRequestDto request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var described = await DescribeAsync(mount, cancellationToken).ConfigureAwait(false);
        var database = described.Databases.FirstOrDefault(d =>
                string.Equals(d.Instance, request.Instance, StringComparison.OrdinalIgnoreCase) && string.Equals(d.Name, request.Database, StringComparison.Ordinal))
            ?? throw new KeyNotFoundException($"The restore point has no SQL database {request.Instance}\\{request.Database}.");
        var broken = database.Files.Where(f => f.Problem is not null || f.Volume is null || f.GuestPath is null).ToList();
        if (broken.Count > 0 || database.Files.Count == 0)
        {
            throw new InvalidOperationException(
                $"The files of SQL database {database.Instance}\\{database.Name} cannot be exported: " +
                (broken.Count == 0 ? "the backup lists no files." : string.Join("; ", broken.Select(f => $"{f.Path}: {f.Problem ?? "not found"}"))));
        }

        var names = ZipNames.Unique([.. database.Files.Select(f => f.Path[(f.Path.LastIndexOf('\\') + 1)..])]);
        var files = database.Files.Select((f, i) => (f.Volume!, GuestPath.Parse(f.GuestPath!), names[i])).ToList();
        var script = ($"{ZipNames.Sanitize(database.Name)}-attach.sql", AttachScript(database.Name, names));
        var created = mount.Point.CreatedUtc.ToLocalTime();
        return downloads.PrepareFiles(
            mount,
            files,
            [script],
            $"{mount.Point.VmName}_{created:yyyy-MM-dd_HHmm}_{database.Instance}_{database.Name}.zip",
            $"the files of SQL database {database.Instance}\\{database.Name} ({database.Files.Count} file(s))",
            request.TimeZone);
    }

    /// <summary>A T-SQL script that attaches the exported files; names are quoted, since they come from the backup.</summary>
    internal static string AttachScript(string database, IReadOnlyList<string> fileNames)
    {
        const string folder = @"D:\Restore";
        var text = new System.Text.StringBuilder();
        // The name comes from the backup: no control characters in the comment, so it cannot end the comment line.
        var shown = new string([.. database.Select(c => char.IsControl(c) ? '?' : c)]);
        text.Append($"-- Attach SQL Server database {shown} from the files in this ZIP file (exported by backdafuckup).\r\n");
        text.Append("-- 1. Copy the files into a folder that the SQL Server service account may read and write.\r\n");
        text.Append($"-- 2. Replace {folder} below with that folder, and the database name if a database of that name exists already.\r\n");
        text.Append("-- 3. Run the script with a login that may create databases (dbcreator or sysadmin).\r\n");
        text.Append("-- The files are application- or crash-consistent copies; SQL Server runs recovery when it attaches them.\r\n");
        text.Append($"CREATE DATABASE {QuoteName(database)} ON\r\n");
        text.Append(string.Join(",\r\n", fileNames.Select(n => $"    (FILENAME = N'{(folder + "\\" + n).Replace("'", "''", StringComparison.Ordinal)}')")));
        text.Append("\r\nFOR ATTACH;\r\n");
        return text.ToString();
    }

    /// <summary>
    /// A database of the backup with all its files found, or the reasons it cannot be <paramref name="verb"/> (system
    /// database, FILESTREAM, files missing in the backup, …).
    /// </summary>
    internal static SqlDatabaseDto Restorable(SqlMountDto described, string instance, string name, string vmName, DateTimeOffset created, string verb)
    {
        var database = described.Databases.FirstOrDefault(d =>
                string.Equals(d.Instance, instance, StringComparison.OrdinalIgnoreCase) && string.Equals(d.Name, name, StringComparison.Ordinal))
            ?? throw new KeyNotFoundException($"The restore point of {vmName} from {created.ToLocalTime():g} has no SQL Server database {instance}\\{name}.");
        if (!database.Restorable)
        {
            throw new InvalidOperationException($"SQL Server database {instance}\\{name} cannot be {verb}: {string.Join("; ", database.Problems)}");
        }

        var broken = database.Files.Where(f => f.Problem is not null || f.Volume is null || f.GuestPath is null).ToList();
        if (broken.Count > 0 || database.Files.Count == 0)
        {
            throw new InvalidOperationException(
                $"SQL Server database {instance}\\{name} cannot be {verb}: " +
                (broken.Count == 0 ? "the backup lists no files." : string.Join("; ", broken.Select(f => $"{f.Path}: {f.Problem ?? "not found in the backup"}"))));
        }

        return database;
    }

    /// <summary>T-SQL QUOTENAME for identifiers: [name] with "]" doubled.</summary>
    internal static string QuoteName(string name) => "[" + name.Replace("]", "]]", StringComparison.Ordinal) + "]";

    internal static SqlMountDto Describe(GuestImage image, StoredGuestMetadata? stored)
    {
        var warnings = new List<string>();
        var instances = ReadInstances(image, warnings);
        var databases = (stored?.SqlDatabases ?? []).Select(d => DescribeDatabase(image, d)).ToList();
        foreach (var name in databases.Select(d => d.Instance).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (instances.All(i => !string.Equals(i.Name, name, StringComparison.OrdinalIgnoreCase)))
            {
                instances.Add(new SqlInstanceDto(name, null, null, null, null, null, null, null, null, null));
            }
        }

        if (stored is null)
        {
            warnings.Add("The restore point has no guest metadata: it was taken without the backdafuckup agent, so its SQL databases are not known.");
        }

        return new SqlMountDto(instances, databases, warnings);
    }

    private static SqlDatabaseDto DescribeDatabase(GuestImage image, StoredSqlDatabase database)
    {
        var problems = new List<string>();
        var files = new List<SqlFileDto>();
        var classified = database.DataFiles.Count + database.LogFiles.Count + database.OtherFiles.Count > 0;
        if (classified && database.DataFiles.Count == 0 && database.LogFiles.Count == 0)
        {
            // The SQL Server writer reports its databases as file groups with plain file lists (FILE_LIST), not as database
            // and log files: every file arrives as "other". Page 0 of each file tells them apart.
            foreach (var description in database.OtherFiles)
            {
                AddUnclassified(description);
            }
        }
        else if (classified)
        {
            Add(database.DataFiles, "data");
            Add(database.LogFiles, "log");
            Add(database.OtherFiles, "other");
        }
        else
        {
            // Guest metadata v1: a flat list; .ldf is the usual extension of log files.
            foreach (var path in database.Files)
            {
                files.Add(DescribeFile(image, path, path.EndsWith(".ldf", StringComparison.OrdinalIgnoreCase) ? "log" : "data"));
            }
        }

        files = MarkPrimary(image, files);
        if (SystemDatabases.Contains(database.Database, StringComparer.OrdinalIgnoreCase))
        {
            problems.Add("it is a system database; restore it together with the VM");
        }

        if (!files.Any(f => f.Kind == "data"))
        {
            problems.Add("the backup metadata lists no data file");
        }

        if (!files.Any(f => f.Kind == "log"))
        {
            problems.Add("the backup metadata lists no log file");
        }

        problems.AddRange(files.Where(f => f.Problem is not null).Select(f => $"{f.Path}: {f.Problem}"));
        return new SqlDatabaseDto(
            database.Instance,
            database.Database,
            database.Consistency,
            database.ConsistencyReason,
            problems.Count == 0,
            problems,
            files,
            files.Sum(f => f.SizeBytes ?? 0));

        void Add(List<GuestSqlFile> descriptions, string kind)
        {
            foreach (var description in descriptions)
            {
                var path = description.FullPath();
                if (path is null)
                {
                    var folder = description.Path.TrimEnd('\\') + "\\" + description.Spec;
                    files.Add(new SqlFileDto(folder, kind, false, null, null, null,
                        kind == "data"
                            ? "a FILESTREAM or In-Memory OLTP container (a whole folder); databases with containers are not supported yet"
                            : "a folder of files (e.g. a full-text catalog); databases with such files are not supported yet"));
                    continue;
                }

                var file = DescribeFile(image, path, kind);
                files.Add(kind == "other" && file.Problem is null
                    ? file with { Problem = "a file other than data and log files (e.g. a full-text catalog); not supported yet" }
                    : file);
            }
        }

        // A folder (FILESTREAM or In-Memory OLTP container) or a file classified by its header: file id 1 is the primary data
        // file and 2 the first log file; higher ids (more data or log files) go by the extension, .ldf being a log file.
        void AddUnclassified(GuestSqlFile description)
        {
            var path = description.FullPath();
            if (path is null)
            {
                Add([description], "data");
                return;
            }

            var file = DescribeFile(image, path, "data");
            if (file.Problem is not null)
            {
                files.Add(file with { Kind = path.EndsWith(".ldf", StringComparison.OrdinalIgnoreCase) ? "log" : "data" });
                return;
            }

            var id = FileId(image, file);
            var log = id == 2 || (id != 1 && path.EndsWith(".ldf", StringComparison.OrdinalIgnoreCase));
            files.Add(file with { Kind = log ? "log" : "data" });
        }
    }

    /// <summary>Finds the file in the backup by its drive letter and reads its size.</summary>
    private static SqlFileDto DescribeFile(GuestImage image, string path, string kind)
    {
        if (path.Length < 4 || path[1] != ':' || path[2] != '\\' || !char.IsAsciiLetter(path[0]))
        {
            return new SqlFileDto(path, kind, false, null, null, null, "not on a drive letter (e.g. a volume mounted into a folder); not supported yet");
        }

        var letter = char.ToUpperInvariant(path[0]) + ":";
        var volume = image.Volumes.FirstOrDefault(v => string.Equals(v.MountPoint, letter, StringComparison.OrdinalIgnoreCase));
        if (volume is null)
        {
            return new SqlFileDto(path, kind, false, null, null, null, $"the backup has no volume with drive letter {letter}");
        }

        if (!volume.Readable)
        {
            return new SqlFileDto(path, kind, false, null, volume.Id, null, $"volume {letter} cannot be read: {volume.Reason}");
        }

        GuestPath guestPath;
        try
        {
            guestPath = GuestPath.Parse(path[3..].Replace('\\', '/'));
        }
        catch (ArgumentException ex)
        {
            return new SqlFileDto(path, kind, false, null, volume.Id, null, ex.Message);
        }

        try
        {
            var entry = image.OpenFileSystem(volume.Id).Stat(guestPath);
            if (entry.Kind != GuestEntryKind.File)
            {
                return new SqlFileDto(path, kind, false, null, volume.Id, guestPath.ToString(), "it is not a regular file in the backup");
            }

            return new SqlFileDto(path, kind, false, entry.Size, volume.Id, guestPath.ToString(), entry.Unsupported);
        }
        catch (FileNotFoundException)
        {
            return new SqlFileDto(path, kind, false, null, volume.Id, guestPath.ToString(), "it does not exist in the restore point");
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or NotSupportedException)
        {
            return new SqlFileDto(path, kind, false, null, volume.Id, guestPath.ToString(), $"it cannot be read: {ex.Message}");
        }
    }

    /// <summary>
    /// The primary data file goes first in CREATE DATABASE … FOR ATTACH. With several data files, page 0 of each tells:
    /// its page header holds the file id at offset 0x24, and the primary file has id 1.
    /// </summary>
    private static List<SqlFileDto> MarkPrimary(GuestImage image, List<SqlFileDto> files)
    {
        var data = files.Where(f => f.Kind == "data" && f.Problem is null).ToList();
        SqlFileDto? primary = data.Count == 1 ? data[0] : null;
        if (primary is null && data.Count > 1)
        {
            primary = data.FirstOrDefault(f => FileId(image, f) == 1)
                ?? data.FirstOrDefault(f => f.Path.EndsWith(".mdf", StringComparison.OrdinalIgnoreCase))
                ?? data[0];
        }

        return primary is null ? files : [primary with { Primary = true }, .. files.Where(f => !ReferenceEquals(f, primary))];
    }

    private static int FileId(GuestImage image, SqlFileDto file)
    {
        try
        {
            using var stream = image.OpenFileSystem(file.Volume!).OpenRead(GuestPath.Parse(file.GuestPath!));
            Span<byte> header = stackalloc byte[0x26];
            stream.ReadExactly(header);
            return header[1] == 15 ? BitConverter.ToUInt16(header[0x24..]) : -1;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or EndOfStreamException or ArgumentException or NotSupportedException)
        {
            return -1;
        }
    }

    /// <summary>Instances from SOFTWARE\Microsoft\Microsoft SQL Server, the service account from SYSTEM\…\Services.</summary>
    private static List<SqlInstanceDto> ReadInstances(GuestImage image, List<string> warnings)
    {
        var result = new List<SqlInstanceDto>();
        var system = image.Volumes.FirstOrDefault(v => v.Readable && v.FileSystem == "NTFS" && Exists(image, v, SoftwareHive));
        if (system is null)
        {
            return result;
        }

        try
        {
            var fs = image.OpenFileSystem(system.Id);
            using var softwareStream = fs.OpenRead(GuestPath.Parse(SoftwareHive));
            using var software = new RegistryHive(softwareStream, Ownership.None);
            var names = software.Root.OpenSubKey(SqlRoot + @"\Instance Names\SQL");
            if (names is null)
            {
                return result;
            }

            Dictionary<string, string?> accounts = [];
            if (Exists(image, system, SystemHive))
            {
                using var systemStream = fs.OpenRead(GuestPath.Parse(SystemHive));
                using var systemHive = new RegistryHive(systemStream, Ownership.None);
                var current = systemHive.Root.OpenSubKey("Select")?.GetValue("Current") is int set ? set : 1;
                var services = systemHive.Root.OpenSubKey($@"ControlSet{current:000}\Services");
                foreach (var name in names.GetValueNames())
                {
                    var service = ServiceName(name);
                    accounts[name] = services?.OpenSubKey(service)?.GetValue("ObjectName") as string;
                }
            }

            foreach (var name in names.GetValueNames().Take(64))
            {
                var id = names.GetValue(name) as string;
                var setup = id is null ? null : software.Root.OpenSubKey($@"{SqlRoot}\{id}\Setup");
                var server = id is null ? null : software.Root.OpenSubKey($@"{SqlRoot}\{id}\MSSQLServer");
                var version = setup?.GetValue("Version") as string;
                var dataRoot = setup?.GetValue("SQLDataRoot") as string;
                result.Add(new SqlInstanceDto(
                    name,
                    id,
                    version,
                    Product(version),
                    setup?.GetValue("Edition") as string,
                    ServiceName(name),
                    accounts.GetValueOrDefault(name),
                    server?.GetValue("DefaultData") as string ?? (dataRoot is null ? null : dataRoot.TrimEnd('\\') + @"\DATA"),
                    server?.GetValue("DefaultLog") as string ?? (dataRoot is null ? null : dataRoot.TrimEnd('\\') + @"\DATA"),
                    server?.GetValue("BackupDirectory") as string));
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or NotSupportedException or IndexOutOfRangeException or InvalidCastException)
        {
            warnings.Add($"The SQL Server instances could not be read from the registry in the backup ({SoftwareHive} on volume {system.MountPoint ?? system.Id}): {ex.Message}");
        }

        return result;
    }

    private static string ServiceName(string instance) =>
        string.Equals(instance, "MSSQLSERVER", StringComparison.OrdinalIgnoreCase) ? "MSSQLSERVER" : "MSSQL$" + instance;

    /// <summary>"16.0.1000.6" → "SQL Server 2022".</summary>
    internal static string? Product(string? version)
    {
        var dot = version?.IndexOf('.') ?? -1;
        if (dot <= 0 || !int.TryParse(version![..dot], NumberStyles.None, CultureInfo.InvariantCulture, out var major))
        {
            return null;
        }

        return major switch
        {
            10 => "SQL Server 2008",
            11 => "SQL Server 2012",
            12 => "SQL Server 2014",
            13 => "SQL Server 2016",
            14 => "SQL Server 2017",
            15 => "SQL Server 2019",
            16 => "SQL Server 2022",
            17 => "SQL Server 2025",
            _ => $"SQL Server (version {major})",
        };
    }

    private static bool Exists(GuestImage image, FileLevelVolume volume, string path)
    {
        try
        {
            return image.OpenFileSystem(volume.Id).Stat(GuestPath.Parse(path)).Kind == GuestEntryKind.File;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }
    }
}
