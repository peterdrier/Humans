using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace Humans.Stripe.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialStripeMirror : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "stripe_balance_transactions",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    AccountId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Type = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ReportingCategory = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    Amount = table.Column<long>(type: "bigint", nullable: false),
                    Fee = table.Column<long>(type: "bigint", nullable: false),
                    Net = table.Column<long>(type: "bigint", nullable: false),
                    Created = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    AvailableOn = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    Description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    SourceId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    PayoutId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    PaymentIntentId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    CustomerName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    CustomerEmail = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    Metadata = table.Column<string>(type: "jsonb", nullable: true),
                    Raw = table.Column<string>(type: "jsonb", nullable: false),
                    SyncedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_stripe_balance_transactions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "stripe_balances",
                columns: table => new
                {
                    AccountId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    Available = table.Column<long>(type: "bigint", nullable: false),
                    Pending = table.Column<long>(type: "bigint", nullable: false),
                    SyncedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_stripe_balances", x => new { x.AccountId, x.Currency });
                });

            migrationBuilder.CreateTable(
                name: "stripe_payouts",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    AccountId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Amount = table.Column<long>(type: "bigint", nullable: false),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Automatic = table.Column<bool>(type: "boolean", nullable: false),
                    Method = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    Created = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    ArrivalDate = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    BalanceTransactionId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    FailureCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    FailureMessage = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    StatementDescriptor = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    SyncedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_stripe_payouts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "stripe_sync_states",
                columns: table => new
                {
                    AccountId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    SyncStatus = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    LastSyncAt = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    StatusChangedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    LastCount = table.Column<int>(type: "integer", nullable: false),
                    LastError = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_stripe_sync_states", x => x.AccountId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_stripe_balance_transactions_AccountId",
                table: "stripe_balance_transactions",
                column: "AccountId");

            migrationBuilder.CreateIndex(
                name: "IX_stripe_payouts_AccountId",
                table: "stripe_payouts",
                column: "AccountId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "stripe_balance_transactions");

            migrationBuilder.DropTable(
                name: "stripe_balances");

            migrationBuilder.DropTable(
                name: "stripe_payouts");

            migrationBuilder.DropTable(
                name: "stripe_sync_states");
        }
    }
}
