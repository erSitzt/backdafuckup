using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Backdafuckup.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class KubernetesPlatforms : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "detected_platforms",
                table: "connections",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "platform_override",
                table: "connections",
                type: "jsonb",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "detected_platforms",
                table: "connections");

            migrationBuilder.DropColumn(
                name: "platform_override",
                table: "connections");
        }
    }
}
