using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PaymentsModule.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddForceCloseAuditToCashShift : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ClosedByAdminId",
                table: "CashShifts",
                type: "nvarchar(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ForceCloseReason",
                table: "CashShifts",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ClosedByAdminId",
                table: "CashShifts");

            migrationBuilder.DropColumn(
                name: "ForceCloseReason",
                table: "CashShifts");
        }
    }
}
