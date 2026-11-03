using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Backdafuckup.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSqlDatabaseCount : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "sql_database_count",
                table: "restore_points",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "sql_database_count",
                table: "restore_points");
        }
    }
}
