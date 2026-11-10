using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Backdafuckup.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSnapshotWithMemory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "memory_image_bytes",
                table: "restore_points",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "with_memory",
                table: "job_objects",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "memory_image_bytes",
                table: "restore_points");

            migrationBuilder.DropColumn(
                name: "with_memory",
                table: "job_objects");
        }
    }
}
