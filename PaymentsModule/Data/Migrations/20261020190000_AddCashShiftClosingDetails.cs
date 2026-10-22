using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PaymentsModule.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddCashShiftClosingDetails : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "CardTotal",
                table: "CashShifts",
                type: "decimal(18,2)",
                precision: 18,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "DeclaredCash",
                table: "CashShifts",
                type: "decimal(18,2)",
                precision: 18,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "Difference",
                table: "CashShifts",
                type: "decimal(18,2)",
                precision: 18,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "ExpectedCash",
                table: "CashShifts",
                type: "decimal(18,2)",
                precision: 18,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "TipsTotal",
                table: "CashShifts",
                type: "decimal(18,2)",
                precision: 18,
                scale: 2,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_CashShifts_CashierId",
                table: "CashShifts",
                column: "CashierId",
                unique: true,
                filter: "[Status] = 'Open'");

            migrationBuilder.Sql(
                "ALTER DATABASE CURRENT SET READ_COMMITTED_SNAPSHOT ON WITH ROLLBACK IMMEDIATE;",
                suppressTransaction: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_CashShifts_CashierId",
                table: "CashShifts");

            migrationBuilder.DropColumn(
                name: "CardTotal",
                table: "CashShifts");

            migrationBuilder.DropColumn(
                name: "DeclaredCash",
                table: "CashShifts");

            migrationBuilder.DropColumn(
                name: "Difference",
                table: "CashShifts");

            migrationBuilder.DropColumn(
                name: "ExpectedCash",
                table: "CashShifts");

            migrationBuilder.DropColumn(
                name: "TipsTotal",
                table: "CashShifts");
        }
    }
}

