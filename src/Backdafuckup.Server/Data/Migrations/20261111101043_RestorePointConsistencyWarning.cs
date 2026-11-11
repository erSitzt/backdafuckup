using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Backdafuckup.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class RestorePointConsistencyWarning : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "consistency_warning",
                table: "restore_points",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "sql_consistent_count",
                table: "restore_points",
                type: "integer",
                nullable: true);

            // Existing restore points: a reason is a warning, except the note of a SQL Server snapshot that recorded every
            // database (the rule for manifests without the flag). The consistent SQL databases come from the backfill at start.
            migrationBuilder.Sql(
                "UPDATE restore_points SET consistency_warning = (consistency_reason IS NOT NULL AND consistency_reason <> " +
                "'SQL Server snapshot: the SQL Server databases are application-consistent; files outside SQL Server are crash-consistent.');");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "consistency_warning",
                table: "restore_points");

            migrationBuilder.DropColumn(
                name: "sql_consistent_count",
                table: "restore_points");
        }
    }
}
