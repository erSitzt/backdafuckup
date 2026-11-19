using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Backdafuckup.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class InstantRecoveryStart : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "network_map",
                table: "instant_recovery_vms",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "connect_nics",
                table: "instant_recoveries",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "folder_id",
                table: "instant_recoveries",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "keep_disk_identity",
                table: "instant_recoveries",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "migration_target",
                table: "instant_recoveries",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "remote_host",
                table: "instant_recoveries",
                type: "character varying(255)",
                maxLength: 255,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "resource_pool_id",
                table: "instant_recoveries",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "network_map",
                table: "instant_recovery_vms");

            migrationBuilder.DropColumn(
                name: "connect_nics",
                table: "instant_recoveries");

            migrationBuilder.DropColumn(
                name: "folder_id",
                table: "instant_recoveries");

            migrationBuilder.DropColumn(
                name: "keep_disk_identity",
                table: "instant_recoveries");

            migrationBuilder.DropColumn(
                name: "migration_target",
                table: "instant_recoveries");

            migrationBuilder.DropColumn(
                name: "remote_host",
                table: "instant_recoveries");

            migrationBuilder.DropColumn(
                name: "resource_pool_id",
                table: "instant_recoveries");
        }
    }
}
