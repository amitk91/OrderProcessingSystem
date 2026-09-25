using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OrderProcessing.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPromotionLease : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "PromotionLease",
                table: "orders",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Orders_Status_Unleased",
                table: "orders",
                column: "Status",
                filter: "\"PromotionLease\" IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Orders_Status_Unleased",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "PromotionLease",
                table: "orders");
        }
    }
}
