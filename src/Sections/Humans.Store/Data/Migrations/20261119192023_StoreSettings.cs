using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Humans.Store.Data.Migrations
{
    /// <inheritdoc />
    public partial class StoreSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "store_settings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false),
                    DepositLiabilityAccountNum = table.Column<int>(type: "integer", nullable: true),
                    SimplifiedInvoiceThresholdEur = table.Column<decimal>(type: "numeric(12,2)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_store_settings", x => x.Id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "store_settings");
        }
    }
}
