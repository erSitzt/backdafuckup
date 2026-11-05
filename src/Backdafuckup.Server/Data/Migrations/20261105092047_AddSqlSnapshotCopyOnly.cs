using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Backdafuckup.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSqlSnapshotCopyOnly : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "sql_snapshot_copy_only",
                table: "jobs",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            // Jobs that already use the SQL Server snapshot keep copy-only backups, as before; new jobs default to full backups.
            migrationBuilder.Sql("UPDATE jobs SET sql_snapshot_copy_only = true WHERE guest_processing_mode = 'SqlSnapshot';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "sql_snapshot_copy_only",
                table: "jobs");
        }
    }
}
