using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PaymentsModule.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddChargeAndShiftDetails : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "ChangeGiven",
                table: "Transactions",
                type: "decimal(18,2)",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<string>(
                name: "TableName",
                table: "Transactions",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CashierName",
                table: "CashShifts",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CloseReason",
                table: "CashShifts",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "FolioCounters",
                columns: table => new
                {
                    Day = table.Column<DateOnly>(type: "date", nullable: false),
                    LastNumber = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FolioCounters", x => x.Day);
                });

            // Folios issued before this migration used the global sequence as suffix. Start each day's counter after
            // the highest suffix already used that (UTC) day, so the daily counter never repeats an existing folio.
            migrationBuilder.Sql("""
                INSERT INTO FolioCounters (Day, LastNumber)
                SELECT CAST(SWITCHOFFSET(CreatedAt, '+00:00') AS date), CAST(MAX(Number) AS int)
                FROM Transactions
                GROUP BY CAST(SWITCHOFFSET(CreatedAt, '+00:00') AS date);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "FolioCounters");

            migrationBuilder.DropColumn(
                name: "ChangeGiven",
                table: "Transactions");

            migrationBuilder.DropColumn(
                name: "TableName",
                table: "Transactions");

            migrationBuilder.DropColumn(
                name: "CashierName",
                table: "CashShifts");

            migrationBuilder.DropColumn(
                name: "CloseReason",
                table: "CashShifts");
        }
    }
}
