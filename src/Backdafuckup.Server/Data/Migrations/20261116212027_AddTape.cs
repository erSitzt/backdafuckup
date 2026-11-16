using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Backdafuckup.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddTape : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "bytes_written",
                table: "tape_media",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<int>(
                name: "file_count",
                table: "tape_media",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "label_name",
                table: "tape_media",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "labelled_utc",
                table: "tape_media",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "last_written_utc",
                table: "tape_media",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "medium_id",
                table: "tape_media",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "pool_id",
                table: "tape_media",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "sequence",
                table: "tape_media",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "set_id",
                table: "tape_media",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "state",
                table: "tape_media",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "Unknown");

            migrationBuilder.AddColumn<Guid>(
                name: "imported_from_tape_copy_id",
                table: "restore_points",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "tape_keys",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    fingerprint = table.Column<string>(type: "character varying(19)", maxLength: 19, nullable: false),
                    protected_kek = table.Column<string>(type: "text", nullable: false),
                    memory_ki_b = table.Column<int>(type: "integer", nullable: false),
                    iterations = table.Column<int>(type: "integer", nullable: false),
                    parallelism = table.Column<int>(type: "integer", nullable: false),
                    salt = table.Column<string>(type: "text", nullable: false),
                    created_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    key_file_downloaded = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_tape_keys", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "media_pools",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    library_id = table.Column<Guid>(type: "uuid", nullable: false),
                    block_size = table.Column<int>(type: "integer", nullable: false),
                    new_set = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    time_zone_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    retention_days = table.Column<int>(type: "integer", nullable: true),
                    worm_only = table.Column<bool>(type: "boolean", nullable: false),
                    encryption = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    key_id = table.Column<Guid>(type: "uuid", nullable: true),
                    export = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    created_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_media_pools", x => x.id);
                    table.ForeignKey(
                        name: "fk_media_pools_tape_keys_key_id",
                        column: x => x.key_id,
                        principalTable: "tape_keys",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_media_pools_tape_libraries_library_id",
                        column: x => x.library_id,
                        principalTable: "tape_libraries",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "media_sets",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    pool_id = table.Column<Guid>(type: "uuid", nullable: false),
                    state = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    created_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    closed_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    expires_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    key_fingerprint = table.Column<string>(type: "character varying(19)", maxLength: 19, nullable: true),
                    index_segments = table.Column<List<string>>(type: "text[]", nullable: true),
                    media = table.Column<int>(type: "integer", nullable: false),
                    bytes = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_media_sets", x => x.id);
                    table.ForeignKey(
                        name: "fk_media_sets_media_pools_pool_id",
                        column: x => x.pool_id,
                        principalTable: "media_pools",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "tape_jobs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    enabled = table.Column<bool>(type: "boolean", nullable: false),
                    pool_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_job_ids = table.Column<List<Guid>>(type: "uuid[]", nullable: false),
                    mode = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    include_sql_backups = table.Column<bool>(type: "boolean", nullable: false),
                    schedule_kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    schedule_time_of_day = table.Column<TimeOnly>(type: "time without time zone", nullable: false),
                    schedule_days_of_week = table.Column<int>(type: "integer", nullable: false),
                    schedule_time_zone_id = table.Column<string>(type: "text", nullable: false),
                    after_job_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_run_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_result = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    next_run_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_tape_jobs", x => x.id);
                    table.ForeignKey(
                        name: "fk_tape_jobs_media_pools_pool_id",
                        column: x => x.pool_id,
                        principalTable: "media_pools",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "tape_copies",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    set_id = table.Column<Guid>(type: "uuid", nullable: false),
                    pool_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    original_id = table.Column<Guid>(type: "uuid", nullable: false),
                    repository_id = table.Column<Guid>(type: "uuid", nullable: false),
                    job_name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    vm_id = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    vm_name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    object_kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    created_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    copied_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    session_id = table.Column<Guid>(type: "uuid", nullable: true),
                    sequence = table.Column<int>(type: "integer", nullable: false),
                    file_number = table.Column<int>(type: "integer", nullable: false),
                    file_start_block = table.Column<long>(type: "bigint", nullable: false),
                    file_id = table.Column<decimal>(type: "numeric(20,0)", nullable: false),
                    needed_sequences = table.Column<List<int>>(type: "integer[]", nullable: false),
                    stored_bytes = table.Column<long>(type: "bigint", nullable: false),
                    manifest = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_tape_copies", x => x.id);
                    table.ForeignKey(
                        name: "fk_tape_copies_media_sets_set_id",
                        column: x => x.set_id,
                        principalTable: "media_sets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "tape_files",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    set_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sequence = table.Column<int>(type: "integer", nullable: false),
                    file_number = table.Column<int>(type: "integer", nullable: false),
                    start_block = table.Column<long>(type: "bigint", nullable: false),
                    file_id = table.Column<decimal>(type: "numeric(20,0)", nullable: false),
                    type = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    blocks = table.Column<long>(type: "bigint", nullable: false),
                    bytes = table.Column<long>(type: "bigint", nullable: false),
                    copy_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_tape_files", x => x.id);
                    table.ForeignKey(
                        name: "fk_tape_files_media_sets_set_id",
                        column: x => x.set_id,
                        principalTable: "media_sets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_tape_media_pool_id",
                table: "tape_media",
                column: "pool_id");

            migrationBuilder.CreateIndex(
                name: "ix_tape_media_set_id",
                table: "tape_media",
                column: "set_id");

            migrationBuilder.CreateIndex(
                name: "ix_media_pools_key_id",
                table: "media_pools",
                column: "key_id");

            migrationBuilder.CreateIndex(
                name: "ix_media_pools_library_id",
                table: "media_pools",
                column: "library_id");

            migrationBuilder.CreateIndex(
                name: "ix_media_pools_name",
                table: "media_pools",
                column: "name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_media_sets_pool_id_state",
                table: "media_sets",
                columns: new[] { "pool_id", "state" });

            migrationBuilder.CreateIndex(
                name: "ix_tape_copies_original_id",
                table: "tape_copies",
                column: "original_id");

            migrationBuilder.CreateIndex(
                name: "ix_tape_copies_set_id",
                table: "tape_copies",
                column: "set_id");

            migrationBuilder.CreateIndex(
                name: "ix_tape_files_set_id_sequence_file_number",
                table: "tape_files",
                columns: new[] { "set_id", "sequence", "file_number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_tape_jobs_name",
                table: "tape_jobs",
                column: "name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_tape_jobs_pool_id",
                table: "tape_jobs",
                column: "pool_id");

            migrationBuilder.CreateIndex(
                name: "ix_tape_keys_fingerprint",
                table: "tape_keys",
                column: "fingerprint",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_tape_keys_name",
                table: "tape_keys",
                column: "name",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "tape_copies");

            migrationBuilder.DropTable(
                name: "tape_files");

            migrationBuilder.DropTable(
                name: "tape_jobs");

            migrationBuilder.DropTable(
                name: "media_sets");

            migrationBuilder.DropTable(
                name: "media_pools");

            migrationBuilder.DropTable(
                name: "tape_keys");

            migrationBuilder.DropIndex(
                name: "ix_tape_media_pool_id",
                table: "tape_media");

            migrationBuilder.DropIndex(
                name: "ix_tape_media_set_id",
                table: "tape_media");

            migrationBuilder.DropColumn(
                name: "bytes_written",
                table: "tape_media");

            migrationBuilder.DropColumn(
                name: "file_count",
                table: "tape_media");

            migrationBuilder.DropColumn(
                name: "label_name",
                table: "tape_media");

            migrationBuilder.DropColumn(
                name: "labelled_utc",
                table: "tape_media");

            migrationBuilder.DropColumn(
                name: "last_written_utc",
                table: "tape_media");

            migrationBuilder.DropColumn(
                name: "medium_id",
                table: "tape_media");

            migrationBuilder.DropColumn(
                name: "pool_id",
                table: "tape_media");

            migrationBuilder.DropColumn(
                name: "sequence",
                table: "tape_media");

            migrationBuilder.DropColumn(
                name: "set_id",
                table: "tape_media");

            migrationBuilder.DropColumn(
                name: "state",
                table: "tape_media");

            migrationBuilder.DropColumn(
                name: "imported_from_tape_copy_id",
                table: "restore_points");
        }
    }
}
