using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PaymentsModule.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddTableNameToPaymentTransaction : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "TableName",
                table: "Transactions",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "TableName",
                table: "Transactions");
        }
    }
}
