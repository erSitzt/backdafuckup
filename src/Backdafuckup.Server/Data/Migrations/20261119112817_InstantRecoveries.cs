using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Backdafuckup.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class InstantRecoveries : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "instant_recoveries",
                columns: table => new
                {
                    session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    connection_id = table.Column<Guid>(type: "uuid", nullable: false),
                    host_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    host_name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    datastore_name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    datastore_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    export_path = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    export_id = table.Column<long>(type: "bigint", nullable: false),
                    allowed_clients = table.Column<string>(type: "jsonb", nullable: true),
                    overlay_directory = table.Column<string>(type: "text", nullable: false),
                    overlay_limit_bytes = table.Column<long>(type: "bigint", nullable: true),
                    state = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    migrate_task_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    last_io_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    message = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_instant_recoveries", x => x.session_id);
                    table.ForeignKey(
                        name: "fk_instant_recoveries_sessions_session_id",
                        column: x => x.session_id,
                        principalTable: "sessions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "instant_recovery_vms",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    restore_point_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    directory = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    vm_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    instance_uuid = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    disk_files = table.Column<string>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_instant_recovery_vms", x => x.id);
                    table.ForeignKey(
                        name: "fk_instant_recovery_vms_instant_recoveries_session_id",
                        column: x => x.session_id,
                        principalTable: "instant_recoveries",
                        principalColumn: "session_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_instant_recoveries_export_id",
                table: "instant_recoveries",
                column: "export_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_instant_recoveries_state",
                table: "instant_recoveries",
                column: "state");

            migrationBuilder.CreateIndex(
                name: "ix_instant_recovery_vms_restore_point_id",
                table: "instant_recovery_vms",
                column: "restore_point_id");

            migrationBuilder.CreateIndex(
                name: "ix_instant_recovery_vms_session_id",
                table: "instant_recovery_vms",
                column: "session_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "instant_recovery_vms");

            migrationBuilder.DropTable(
                name: "instant_recoveries");
        }
    }
}
