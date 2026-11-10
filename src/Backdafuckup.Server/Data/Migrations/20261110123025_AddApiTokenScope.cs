using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Backdafuckup.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddApiTokenScope : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "scope",
                table: "api_tokens",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "full");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "scope",
                table: "api_tokens");
        }
    }
}
