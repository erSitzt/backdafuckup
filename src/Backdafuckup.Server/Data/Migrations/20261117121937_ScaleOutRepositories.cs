using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Backdafuckup.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class ScaleOutRepositories : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "extent_state",
                table: "repositories",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "Active");

            migrationBuilder.AddColumn<bool>(
                name: "maintenance",
                table: "repositories",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "maintenance_since_utc",
                table: "repositories",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "scale_out_id",
                table: "repositories",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "scale_out_settings",
                table: "repositories",
                type: "jsonb",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "chain_placements",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    scale_out_id = table.Column<Guid>(type: "uuid", nullable: false),
                    job_id = table.Column<Guid>(type: "uuid", nullable: false),
                    object_native_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    extent_id = table.Column<Guid>(type: "uuid", nullable: false),
                    placed_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    move_session_id = table.Column<Guid>(type: "uuid", nullable: true),
                    estimate_bytes = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_chain_placements", x => x.id);
                    table.ForeignKey(
                        name: "fk_chain_placements_jobs_job_id",
                        column: x => x.job_id,
                        principalTable: "jobs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_chain_placements_repositories_extent_id",
                        column: x => x.extent_id,
                        principalTable: "repositories",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_chain_placements_repositories_scale_out_id",
                        column: x => x.scale_out_id,
                        principalTable: "repositories",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "repository_move_items",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    scale_out_id = table.Column<Guid>(type: "uuid", nullable: false),
                    job_id = table.Column<Guid>(type: "uuid", nullable: true),
                    object_native_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    object_type = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    object_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_extent_id = table.Column<Guid>(type: "uuid", nullable: false),
                    target_extent_id = table.Column<Guid>(type: "uuid", nullable: false),
                    state = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    bytes_written = table.Column<long>(type: "bigint", nullable: false),
                    target_path = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    error = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    created_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_repository_move_items", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_repositories_scale_out_id",
                table: "repositories",
                column: "scale_out_id");

            migrationBuilder.CreateIndex(
                name: "ix_chain_placements_extent_id",
                table: "chain_placements",
                column: "extent_id");

            migrationBuilder.CreateIndex(
                name: "ix_chain_placements_job_id_object_native_id",
                table: "chain_placements",
                columns: new[] { "job_id", "object_native_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_chain_placements_scale_out_id",
                table: "chain_placements",
                column: "scale_out_id");

            migrationBuilder.CreateIndex(
                name: "ix_repository_move_items_object_id",
                table: "repository_move_items",
                column: "object_id");

            migrationBuilder.CreateIndex(
                name: "ix_repository_move_items_source_extent_id_state",
                table: "repository_move_items",
                columns: new[] { "source_extent_id", "state" });

            migrationBuilder.CreateIndex(
                name: "ix_repository_move_items_target_extent_id_state",
                table: "repository_move_items",
                columns: new[] { "target_extent_id", "state" });

            migrationBuilder.AddForeignKey(
                name: "fk_repositories_repositories_scale_out_id",
                table: "repositories",
                column: "scale_out_id",
                principalTable: "repositories",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_repositories_repositories_scale_out_id",
                table: "repositories");

            migrationBuilder.DropTable(
                name: "chain_placements");

            migrationBuilder.DropTable(
                name: "repository_move_items");

            migrationBuilder.DropIndex(
                name: "ix_repositories_scale_out_id",
                table: "repositories");

            migrationBuilder.DropColumn(
                name: "extent_state",
                table: "repositories");

            migrationBuilder.DropColumn(
                name: "maintenance",
                table: "repositories");

            migrationBuilder.DropColumn(
                name: "maintenance_since_utc",
                table: "repositories");

            migrationBuilder.DropColumn(
                name: "scale_out_id",
                table: "repositories");

            migrationBuilder.DropColumn(
                name: "scale_out_settings",
                table: "repositories");
        }
    }
}
