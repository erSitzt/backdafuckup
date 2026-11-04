using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Backdafuckup.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSqlSnapshotGuestProcessing : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "guest_processing_mode",
                table: "jobs",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                // Existing jobs keep VSS.
                defaultValue: "Vss");

            migrationBuilder.AddColumn<Guid>(
                name: "sql_credential_id",
                table: "jobs",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_jobs_sql_credential_id",
                table: "jobs",
                column: "sql_credential_id");

            migrationBuilder.AddForeignKey(
                name: "fk_jobs_credentials_sql_credential_id",
                table: "jobs",
                column: "sql_credential_id",
                principalTable: "credentials",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_jobs_credentials_sql_credential_id",
                table: "jobs");

            migrationBuilder.DropIndex(
                name: "ix_jobs_sql_credential_id",
                table: "jobs");

            migrationBuilder.DropColumn(
                name: "guest_processing_mode",
                table: "jobs");

            migrationBuilder.DropColumn(
                name: "sql_credential_id",
                table: "jobs");
        }
    }
}
