using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Backdafuckup.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class KubernetesPodVolumes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "kubernetes_pod_volumes",
                table: "jobs",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "usage_secret",
                table: "connections",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "usage_source",
                table: "connections",
                type: "jsonb",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "kubernetes_pod_volumes",
                table: "jobs");

            migrationBuilder.DropColumn(
                name: "usage_secret",
                table: "connections");

            migrationBuilder.DropColumn(
                name: "usage_source",
                table: "connections");
        }
    }
}
