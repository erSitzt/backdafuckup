using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Backdafuckup.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class SessionObjectKind : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "object_kind",
                table: "sessions",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            // Earlier sessions: backups by the kind of their job's connection, restores by their restore point, file
            // browsers by the name of a namespace's restore point in their name (cosmetic: the console's icons).
            migrationBuilder.Sql("""
                UPDATE sessions s SET object_kind = CASE WHEN c.kind = 'Kubernetes' THEN 'KubernetesNamespace' ELSE 'Vm' END
                FROM jobs j JOIN connections c ON c.id = j.connection_id
                WHERE s.kind = 'Backup' AND s.job_id = j.id;

                UPDATE sessions s SET object_kind = r.object_kind
                FROM restore_points r
                WHERE s.kind IN ('Restore', 'FileRestore')
                  AND r.id::text = substring(s.parameters from '"RestorePointId":"([0-9a-f-]{36})"');

                UPDATE sessions s SET object_kind = 'KubernetesNamespace'
                WHERE s.kind = 'FileBrowse' AND EXISTS (
                  SELECT 1 FROM restore_points r
                  WHERE r.object_kind = 'KubernetesNamespace' AND r.repository_id = s.repository_id
                    AND left(s.name, length('Browse files ' || r.vm_name || ' (')) = 'Browse files ' || r.vm_name || ' (');

                UPDATE sessions SET object_kind = 'Vm'
                WHERE object_kind IS NULL AND kind IN ('Backup', 'Restore', 'FileRestore', 'FileBrowse');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "object_kind",
                table: "sessions");
        }
    }
}
