using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RepoPilot.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class EvaluationApprovalSplit : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "InteractiveApprovals",
                table: "evaluation_runs",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "ProgrammaticApprovals",
                table: "evaluation_runs",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "InteractiveApprovals",
                table: "evaluation_runs");

            migrationBuilder.DropColumn(
                name: "ProgrammaticApprovals",
                table: "evaluation_runs");
        }
    }
}
