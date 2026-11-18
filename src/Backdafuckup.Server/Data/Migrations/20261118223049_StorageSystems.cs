using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Backdafuckup.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class StorageSystems : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "storage_system_id",
                table: "provider_artifacts",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "storage_systems",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    address = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    port = table.Column<int>(type: "integer", nullable: false),
                    credential_id = table.Column<Guid>(type: "uuid", nullable: false),
                    trust_mode = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    pinned_spki = table.Column<string>(type: "jsonb", nullable: true),
                    options = table.Column<string>(type: "jsonb", nullable: false),
                    facts = table.Column<string>(type: "jsonb", nullable: true),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    last_error = table.Column<string>(type: "text", nullable: true),
                    last_checked_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_storage_systems", x => x.id);
                    table.ForeignKey(
                        name: "fk_storage_systems_credentials_credential_id",
                        column: x => x.credential_id,
                        principalTable: "credentials",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "storage_volumes",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    storage_system_id = table.Column<Guid>(type: "uuid", nullable: false),
                    volume_uid = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    volume_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    wwn = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    size_bytes = table.Column<long>(type: "bigint", nullable: false),
                    connection_id = table.Column<Guid>(type: "uuid", nullable: true),
                    storage_native_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    storage_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    layout = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    enabled = table.Column<bool>(type: "boolean", nullable: false),
                    refusal = table.Column<string>(type: "text", nullable: true),
                    updated_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_storage_volumes", x => x.id);
                    table.ForeignKey(
                        name: "fk_storage_volumes_storage_systems_storage_system_id",
                        column: x => x.storage_system_id,
                        principalTable: "storage_systems",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_storage_systems_credential_id",
                table: "storage_systems",
                column: "credential_id");

            migrationBuilder.CreateIndex(
                name: "ix_storage_systems_name",
                table: "storage_systems",
                column: "name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_storage_volumes_connection_id_storage_native_id",
                table: "storage_volumes",
                columns: new[] { "connection_id", "storage_native_id" });

            migrationBuilder.CreateIndex(
                name: "ix_storage_volumes_storage_system_id_volume_uid",
                table: "storage_volumes",
                columns: new[] { "storage_system_id", "volume_uid" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "storage_volumes");

            migrationBuilder.DropTable(
                name: "storage_systems");

            migrationBuilder.DropColumn(
                name: "storage_system_id",
                table: "provider_artifacts");
        }
    }
}
