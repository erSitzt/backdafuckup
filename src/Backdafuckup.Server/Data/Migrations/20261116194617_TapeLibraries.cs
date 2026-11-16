using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Backdafuckup.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class TapeLibraries : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "tape_libraries",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    host = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    changer_serial = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    vendor = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    product = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    first_slot = table.Column<int>(type: "integer", nullable: false),
                    slots = table.Column<int>(type: "integer", nullable: false),
                    first_mail_slot = table.Column<int>(type: "integer", nullable: false),
                    mail_slots = table.Column<int>(type: "integer", nullable: false),
                    created_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    inventory_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_error = table.Column<string>(type: "text", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_tape_libraries", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "tape_drives",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    library_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    serial = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    vendor = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    product = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    element_address = table.Column<int>(type: "integer", nullable: true),
                    barcode = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_tape_drives", x => x.id);
                    table.ForeignKey(
                        name: "fk_tape_drives_tape_libraries_library_id",
                        column: x => x.library_id,
                        principalTable: "tape_libraries",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "tape_media",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    barcode = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    library_id = table.Column<Guid>(type: "uuid", nullable: true),
                    kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    location = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    address = table.Column<int>(type: "integer", nullable: true),
                    home_slot = table.Column<int>(type: "integer", nullable: true),
                    medium_serial = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    capacity_bytes = table.Column<long>(type: "bigint", nullable: true),
                    remaining_bytes = table.Column<long>(type: "bigint", nullable: true),
                    first_seen_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    seen_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_tape_media", x => x.id);
                    table.ForeignKey(
                        name: "fk_tape_media_tape_libraries_library_id",
                        column: x => x.library_id,
                        principalTable: "tape_libraries",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "ix_tape_drives_library_id",
                table: "tape_drives",
                column: "library_id");

            migrationBuilder.CreateIndex(
                name: "ix_tape_drives_serial",
                table: "tape_drives",
                column: "serial",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_tape_libraries_changer_serial",
                table: "tape_libraries",
                column: "changer_serial",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_tape_libraries_name",
                table: "tape_libraries",
                column: "name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_tape_media_barcode",
                table: "tape_media",
                column: "barcode",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_tape_media_library_id",
                table: "tape_media",
                column: "library_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "tape_drives");

            migrationBuilder.DropTable(
                name: "tape_media");

            migrationBuilder.DropTable(
                name: "tape_libraries");
        }
    }
}
