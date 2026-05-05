using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Humans.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddStoreSection : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "store_invoices",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrderId = table.Column<Guid>(type: "uuid", nullable: false),
                    HoldedDocId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    HoldedDocNumber = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    IssuedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    IssuedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    RequestPayload = table.Column<string>(type: "jsonb", nullable: false),
                    ResponsePayload = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_store_invoices", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "store_orders",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CampSeasonId = table.Column<Guid>(type: "uuid", nullable: false),
                    Label = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    State = table.Column<int>(type: "integer", nullable: false),
                    CounterpartyName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    CounterpartyVatId = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    CounterpartyAddress = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CounterpartyCountryCode = table.Column<string>(type: "character varying(2)", maxLength: 2, nullable: true),
                    CounterpartyEmail = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true),
                    IssuedInvoiceId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_store_orders", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "store_products",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Year = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    UnitPriceEur = table.Column<decimal>(type: "numeric(12,2)", nullable: false),
                    VatRatePercent = table.Column<decimal>(type: "numeric(5,2)", nullable: false),
                    DepositAmountEur = table.Column<decimal>(type: "numeric(12,2)", nullable: true),
                    OrderableUntil = table.Column<LocalDate>(type: "date", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_store_products", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "store_treasury_sync_state",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    LastSyncAt = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    SyncStatus = table.Column<int>(type: "integer", nullable: false),
                    LastError = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_store_treasury_sync_state", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "store_order_lines",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrderId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProductId = table.Column<Guid>(type: "uuid", nullable: false),
                    Qty = table.Column<int>(type: "integer", nullable: false),
                    UnitPriceSnapshot = table.Column<decimal>(type: "numeric(12,2)", nullable: false),
                    VatRateSnapshot = table.Column<decimal>(type: "numeric(5,2)", nullable: false),
                    DepositAmountSnapshot = table.Column<decimal>(type: "numeric(12,2)", nullable: true),
                    AddedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    AddedByUserId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_store_order_lines", x => x.Id);
                    table.ForeignKey(
                        name: "FK_store_order_lines_store_orders_OrderId",
                        column: x => x.OrderId,
                        principalTable: "store_orders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "store_payments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrderId = table.Column<Guid>(type: "uuid", nullable: false),
                    AmountEur = table.Column<decimal>(type: "numeric(12,2)", nullable: false),
                    Method = table.Column<int>(type: "integer", nullable: false),
                    StripePaymentIntentId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    ExternalRef = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    ReceivedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    RecordedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    Notes = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_store_payments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_store_payments_store_orders_OrderId",
                        column: x => x.OrderId,
                        principalTable: "store_orders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_store_invoices_HoldedDocId",
                table: "store_invoices",
                column: "HoldedDocId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_store_invoices_OrderId",
                table: "store_invoices",
                column: "OrderId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_store_order_lines_OrderId",
                table: "store_order_lines",
                column: "OrderId");

            migrationBuilder.CreateIndex(
                name: "IX_store_order_lines_ProductId",
                table: "store_order_lines",
                column: "ProductId");

            migrationBuilder.CreateIndex(
                name: "IX_store_orders_CampSeasonId",
                table: "store_orders",
                column: "CampSeasonId");

            migrationBuilder.CreateIndex(
                name: "IX_store_orders_State",
                table: "store_orders",
                column: "State");

            migrationBuilder.CreateIndex(
                name: "IX_store_payments_OrderId",
                table: "store_payments",
                column: "OrderId");

            migrationBuilder.CreateIndex(
                name: "IX_store_payments_StripePaymentIntentId",
                table: "store_payments",
                column: "StripePaymentIntentId",
                unique: true,
                filter: "\"StripePaymentIntentId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_store_products_Year_IsActive",
                table: "store_products",
                columns: new[] { "Year", "IsActive" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "store_invoices");

            migrationBuilder.DropTable(
                name: "store_order_lines");

            migrationBuilder.DropTable(
                name: "store_payments");

            migrationBuilder.DropTable(
                name: "store_products");

            migrationBuilder.DropTable(
                name: "store_treasury_sync_state");

            migrationBuilder.DropTable(
                name: "store_orders");
        }
    }
}
