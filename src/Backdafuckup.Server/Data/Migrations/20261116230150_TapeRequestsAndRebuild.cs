using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Backdafuckup.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class TapeRequestsAndRebuild : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "label_encryption",
                table: "tape_media",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "tape_operator_requests",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    library_id = table.Column<Guid>(type: "uuid", nullable: false),
                    drive_id = table.Column<Guid>(type: "uuid", nullable: true),
                    message = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    created_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    resolved_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_tape_operator_requests", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_tape_operator_requests_resolved_utc",
                table: "tape_operator_requests",
                column: "resolved_utc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "tape_operator_requests");

            migrationBuilder.DropColumn(
                name: "label_encryption",
                table: "tape_media");
        }
    }
}
