using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Backdafuckup.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class Proxies : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "proxies",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    created_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    install_token_hash = table.Column<byte[]>(type: "bytea", nullable: true),
                    install_token_expires_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    key_hash = table.Column<byte[]>(type: "bytea", nullable: true),
                    enrolled_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_seen_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_address = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    host_name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    agent_version = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    smbios_uuid = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    bios_serial = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_proxies", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_proxies_name",
                table: "proxies",
                column: "name",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "proxies");
        }
    }
}
