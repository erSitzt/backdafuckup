using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Backdafuckup.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddGuestAgents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "consistency",
                table: "restore_points",
                type: "text",
                nullable: false,
                defaultValue: "crash");

            migrationBuilder.AddColumn<string>(
                name: "consistency_reason",
                table: "restore_points",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "guest_credential_id",
                table: "jobs",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "guest_failure_policy",
                table: "jobs",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "CrashConsistentWithWarning");

            migrationBuilder.AddColumn<bool>(
                name: "guest_processing",
                table: "jobs",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "kind",
                table: "credentials",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "Hypervisor");

            migrationBuilder.CreateTable(
                name: "agents",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    key_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    host_name = table.Column<string>(type: "text", nullable: false),
                    dns_name = table.Column<string>(type: "text", nullable: true),
                    bios_serial = table.Column<string>(type: "text", nullable: true),
                    smbios_uuid = table.Column<string>(type: "text", nullable: true),
                    vm_native_id = table.Column<string>(type: "text", nullable: true),
                    os_name = table.Column<string>(type: "text", nullable: true),
                    os_version = table.Column<string>(type: "text", nullable: true),
                    build = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    agent_version = table.Column<string>(type: "text", nullable: false),
                    files_hash = table.Column<string>(type: "text", nullable: false),
                    vss_provider = table.Column<string>(type: "text", nullable: true),
                    ip_addresses = table.Column<string>(type: "text", nullable: true),
                    remote_address = table.Column<string>(type: "text", nullable: true),
                    enrolled_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_contact_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_agents", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_jobs_guest_credential_id",
                table: "jobs",
                column: "guest_credential_id");

            migrationBuilder.CreateIndex(
                name: "ix_agents_key_hash",
                table: "agents",
                column: "key_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_agents_vm_native_id",
                table: "agents",
                column: "vm_native_id");

            migrationBuilder.AddForeignKey(
                name: "fk_jobs_credentials_guest_credential_id",
                table: "jobs",
                column: "guest_credential_id",
                principalTable: "credentials",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_jobs_credentials_guest_credential_id",
                table: "jobs");

            migrationBuilder.DropTable(
                name: "agents");

            migrationBuilder.DropIndex(
                name: "ix_jobs_guest_credential_id",
                table: "jobs");

            migrationBuilder.DropColumn(
                name: "consistency",
                table: "restore_points");

            migrationBuilder.DropColumn(
                name: "consistency_reason",
                table: "restore_points");

            migrationBuilder.DropColumn(
                name: "guest_credential_id",
                table: "jobs");

            migrationBuilder.DropColumn(
                name: "guest_failure_policy",
                table: "jobs");

            migrationBuilder.DropColumn(
                name: "guest_processing",
                table: "jobs");

            migrationBuilder.DropColumn(
                name: "kind",
                table: "credentials");
        }
    }
}
