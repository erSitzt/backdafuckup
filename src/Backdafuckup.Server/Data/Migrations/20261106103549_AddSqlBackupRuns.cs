using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Backdafuckup.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSqlBackupRuns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "sql_backup_runs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    job_id = table.Column<Guid>(type: "uuid", nullable: false),
                    vm_native_id = table.Column<string>(type: "text", nullable: false),
                    vm_name = table.Column<string>(type: "text", nullable: false),
                    started_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ended_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    state = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    logs = table.Column<int>(type: "integer", nullable: false),
                    fulls = table.Column<int>(type: "integer", nullable: false),
                    bytes = table.Column<long>(type: "bigint", nullable: false),
                    new_stored_bytes = table.Column<long>(type: "bigint", nullable: false),
                    message = table.Column<string>(type: "text", nullable: true),
                    lines = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_sql_backup_runs", x => x.id);
                    table.ForeignKey(
                        name: "fk_sql_backup_runs_sessions_session_id",
                        column: x => x.session_id,
                        principalTable: "sessions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_sql_backup_runs_session_id_started_utc",
                table: "sql_backup_runs",
                columns: new[] { "session_id", "started_utc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "sql_backup_runs");
        }
    }
}
