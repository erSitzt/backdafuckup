using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Backdafuckup.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class RestorePointObjectKind : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Every restore point so far is a VM's.
            migrationBuilder.AddColumn<string>(
                name: "object_kind",
                table: "restore_points",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "Vm");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "object_kind",
                table: "restore_points");
        }
    }
}
