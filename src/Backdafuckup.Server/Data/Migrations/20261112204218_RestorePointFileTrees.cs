using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Backdafuckup.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class RestorePointFileTrees : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "file_count",
                table: "restore_point_disks",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "issue_count",
                table: "restore_point_disks",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "kind",
                table: "restore_point_disks",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "disk");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "file_count",
                table: "restore_point_disks");

            migrationBuilder.DropColumn(
                name: "issue_count",
                table: "restore_point_disks");

            migrationBuilder.DropColumn(
                name: "kind",
                table: "restore_point_disks");
        }
    }
}
