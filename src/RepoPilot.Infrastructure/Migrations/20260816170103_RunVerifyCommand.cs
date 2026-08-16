using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RepoPilot.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class RunVerifyCommand : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "VerifyCommandName",
                table: "runs",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "VerifyCommandName",
                table: "runs");
        }
    }
}
