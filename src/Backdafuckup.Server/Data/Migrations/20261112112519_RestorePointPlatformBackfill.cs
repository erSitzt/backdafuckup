using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Backdafuckup.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class RestorePointPlatformBackfill : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Restore points of namespaces were saved before the cluster's session knew its platform id (kube-system's uid):
            // they take their cluster's, which tells namespaces of the same name in two clusters apart.
            migrationBuilder.Sql("""
                UPDATE restore_points r SET platform_id = c.platform_id
                FROM connections c
                WHERE r.connection_id = c.id AND c.kind = 'Kubernetes' AND r.platform_id = '' AND c.platform_id IS NOT NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Nothing to undo: the platform ids are correct.
        }
    }
}
