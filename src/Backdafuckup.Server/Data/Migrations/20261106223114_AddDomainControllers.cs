using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Backdafuckup.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddDomainControllers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ad_domain",
                table: "restore_points",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "domain_controller",
                table: "restore_points",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "domain",
                table: "agents",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "domain_role",
                table: "agents",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ad_domain",
                table: "restore_points");

            migrationBuilder.DropColumn(
                name: "domain_controller",
                table: "restore_points");

            migrationBuilder.DropColumn(
                name: "domain",
                table: "agents");

            migrationBuilder.DropColumn(
                name: "domain_role",
                table: "agents");
        }
    }
}
