using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Backdafuckup.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSqlBackupCatalog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "sql_backup_sets_recorded",
                table: "restore_points",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "sql_backup_enabled",
                table: "jobs",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<List<string>>(
                name: "sql_backup_excluded_databases",
                table: "jobs",
                type: "text[]",
                nullable: false,
                defaultValueSql: "'{}'::text[]");

            migrationBuilder.AddColumn<bool>(
                name: "sql_backup_full_for_all_databases",
                table: "jobs",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "sql_backup_full_on_chain_break",
                table: "jobs",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<int>(
                name: "sql_backup_full_schedule_days_of_week",
                table: "jobs",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<string>(
                name: "sql_backup_full_schedule_kind",
                table: "jobs",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "Weekly");

            migrationBuilder.AddColumn<TimeOnly>(
                name: "sql_backup_full_schedule_time_of_day",
                table: "jobs",
                type: "time without time zone",
                nullable: false,
                defaultValue: new TimeOnly(20, 0, 0));

            migrationBuilder.AddColumn<string>(
                name: "sql_backup_full_schedule_time_zone_id",
                table: "jobs",
                type: "text",
                nullable: false,
                defaultValue: "UTC");

            migrationBuilder.AddColumn<int>(
                name: "sql_backup_log_interval_minutes",
                table: "jobs",
                type: "integer",
                nullable: false,
                defaultValue: 60);

            migrationBuilder.AddColumn<string>(
                name: "sql_backup_staging_folder",
                table: "jobs",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "sql_backup_system_schedule_days_of_week",
                table: "jobs",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "sql_backup_system_schedule_kind",
                table: "jobs",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "Daily");

            migrationBuilder.AddColumn<TimeOnly>(
                name: "sql_backup_system_schedule_time_of_day",
                table: "jobs",
                type: "time without time zone",
                nullable: false,
                defaultValue: new TimeOnly(20, 0, 0));

            migrationBuilder.AddColumn<string>(
                name: "sql_backup_system_schedule_time_zone_id",
                table: "jobs",
                type: "text",
                nullable: false,
                defaultValue: "UTC");

            // Existing jobs: the SQL backup schedules run in the job's own time zone.
            migrationBuilder.Sql("UPDATE jobs SET sql_backup_full_schedule_time_zone_id = schedule_time_zone_id, sql_backup_system_schedule_time_zone_id = schedule_time_zone_id;");

            migrationBuilder.CreateTable(
                name: "sql_backup_schedules",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    job_id = table.Column<Guid>(type: "uuid", nullable: false),
                    vm_native_id = table.Column<string>(type: "text", nullable: false),
                    next_log_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    next_full_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    next_system_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_run_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    foreign_checked_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_result = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    last_message = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_sql_backup_schedules", x => x.id);
                    table.ForeignKey(
                        name: "fk_sql_backup_schedules_jobs_job_id",
                        column: x => x.job_id,
                        principalTable: "jobs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "sql_backups",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    repository_id = table.Column<Guid>(type: "uuid", nullable: false),
                    job_id = table.Column<Guid>(type: "uuid", nullable: true),
                    session_id = table.Column<Guid>(type: "uuid", nullable: true),
                    platform_id = table.Column<string>(type: "text", nullable: false),
                    vm_native_id = table.Column<string>(type: "text", nullable: false),
                    vm_name = table.Column<string>(type: "text", nullable: false),
                    instance = table.Column<string>(type: "text", nullable: false),
                    kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    state = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    created_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    manifest_path = table.Column<string>(type: "text", nullable: true),
                    file_count = table.Column<int>(type: "integer", nullable: false),
                    bytes = table.Column<long>(type: "bigint", nullable: false),
                    new_stored_bytes = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_sql_backups", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "sql_databases",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    platform_id = table.Column<string>(type: "text", nullable: false),
                    vm_native_id = table.Column<string>(type: "text", nullable: false),
                    vm_name = table.Column<string>(type: "text", nullable: false),
                    instance = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    family_guid = table.Column<Guid>(type: "uuid", nullable: false),
                    database_guid = table.Column<Guid>(type: "uuid", nullable: true),
                    recovery_fork_guid = table.Column<Guid>(type: "uuid", nullable: true),
                    recovery_model = table.Column<string>(type: "text", nullable: true),
                    product_version = table.Column<string>(type: "text", nullable: true),
                    job_id = table.Column<Guid>(type: "uuid", nullable: true),
                    chain_state = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    chain_reason = table.Column<string>(type: "text", nullable: true),
                    chain_changed_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_log_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_full_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    first_seen_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_seen_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_sql_databases", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "sql_backup_sets",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    sql_database_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    repository_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sql_backup_id = table.Column<Guid>(type: "uuid", nullable: true),
                    file_key = table.Column<string>(type: "text", nullable: true),
                    file_name = table.Column<string>(type: "text", nullable: true),
                    restore_point_id = table.Column<Guid>(type: "uuid", nullable: true),
                    backup_set_uuid = table.Column<Guid>(type: "uuid", nullable: false),
                    first_lsn = table.Column<decimal>(type: "numeric(25,0)", precision: 25, scale: 0, nullable: false),
                    last_lsn = table.Column<decimal>(type: "numeric(25,0)", precision: 25, scale: 0, nullable: false),
                    checkpoint_lsn = table.Column<decimal>(type: "numeric(25,0)", precision: 25, scale: 0, nullable: false),
                    database_backup_lsn = table.Column<decimal>(type: "numeric(25,0)", precision: 25, scale: 0, nullable: true),
                    database_guid = table.Column<Guid>(type: "uuid", nullable: true),
                    first_recovery_fork_guid = table.Column<Guid>(type: "uuid", nullable: true),
                    last_recovery_fork_guid = table.Column<Guid>(type: "uuid", nullable: true),
                    fork_point_lsn = table.Column<decimal>(type: "numeric(25,0)", precision: 25, scale: 0, nullable: true),
                    start_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    finish_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    utc_offset_minutes = table.Column<int>(type: "integer", nullable: true),
                    is_copy_only = table.Column<bool>(type: "boolean", nullable: false),
                    position = table.Column<int>(type: "integer", nullable: false),
                    files = table.Column<string>(type: "jsonb", nullable: true),
                    size = table.Column<long>(type: "bigint", nullable: false),
                    sha256 = table.Column<string>(type: "text", nullable: true),
                    product_version = table.Column<string>(type: "text", nullable: true),
                    job_id = table.Column<Guid>(type: "uuid", nullable: true),
                    session_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_sql_backup_sets", x => x.id);
                    table.ForeignKey(
                        name: "fk_sql_backup_sets_restore_points_restore_point_id",
                        column: x => x.restore_point_id,
                        principalTable: "restore_points",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_sql_backup_sets_sql_backups_sql_backup_id",
                        column: x => x.sql_backup_id,
                        principalTable: "sql_backups",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_sql_backup_sets_sql_databases_sql_database_id",
                        column: x => x.sql_database_id,
                        principalTable: "sql_databases",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_sql_backup_schedules_job_id_vm_native_id",
                table: "sql_backup_schedules",
                columns: new[] { "job_id", "vm_native_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_sql_backup_sets_backup_set_uuid",
                table: "sql_backup_sets",
                column: "backup_set_uuid",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_sql_backup_sets_restore_point_id",
                table: "sql_backup_sets",
                column: "restore_point_id");

            migrationBuilder.CreateIndex(
                name: "ix_sql_backup_sets_sql_backup_id",
                table: "sql_backup_sets",
                column: "sql_backup_id");

            migrationBuilder.CreateIndex(
                name: "ix_sql_backup_sets_sql_database_id_last_lsn",
                table: "sql_backup_sets",
                columns: new[] { "sql_database_id", "last_lsn" });

            migrationBuilder.CreateIndex(
                name: "ix_sql_backups_repository_id_vm_native_id_created_utc",
                table: "sql_backups",
                columns: new[] { "repository_id", "vm_native_id", "created_utc" });

            migrationBuilder.CreateIndex(
                name: "ix_sql_databases_platform_id_vm_native_id_instance_name_family",
                table: "sql_databases",
                columns: new[] { "platform_id", "vm_native_id", "instance", "name", "family_guid" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "sql_backup_schedules");

            migrationBuilder.DropTable(
                name: "sql_backup_sets");

            migrationBuilder.DropTable(
                name: "sql_backups");

            migrationBuilder.DropTable(
                name: "sql_databases");

            migrationBuilder.DropColumn(
                name: "sql_backup_sets_recorded",
                table: "restore_points");

            migrationBuilder.DropColumn(
                name: "sql_backup_enabled",
                table: "jobs");

            migrationBuilder.DropColumn(
                name: "sql_backup_excluded_databases",
                table: "jobs");

            migrationBuilder.DropColumn(
                name: "sql_backup_full_for_all_databases",
                table: "jobs");

            migrationBuilder.DropColumn(
                name: "sql_backup_full_on_chain_break",
                table: "jobs");

            migrationBuilder.DropColumn(
                name: "sql_backup_full_schedule_days_of_week",
                table: "jobs");

            migrationBuilder.DropColumn(
                name: "sql_backup_full_schedule_kind",
                table: "jobs");

            migrationBuilder.DropColumn(
                name: "sql_backup_full_schedule_time_of_day",
                table: "jobs");

            migrationBuilder.DropColumn(
                name: "sql_backup_full_schedule_time_zone_id",
                table: "jobs");

            migrationBuilder.DropColumn(
                name: "sql_backup_log_interval_minutes",
                table: "jobs");

            migrationBuilder.DropColumn(
                name: "sql_backup_staging_folder",
                table: "jobs");

            migrationBuilder.DropColumn(
                name: "sql_backup_system_schedule_days_of_week",
                table: "jobs");

            migrationBuilder.DropColumn(
                name: "sql_backup_system_schedule_kind",
                table: "jobs");

            migrationBuilder.DropColumn(
                name: "sql_backup_system_schedule_time_of_day",
                table: "jobs");

            migrationBuilder.DropColumn(
                name: "sql_backup_system_schedule_time_zone_id",
                table: "jobs");
        }
    }
}
