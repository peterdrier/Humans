using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace Humans.Email.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddEmailDailySendCounts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "email_daily_send_counts",
                columns: table => new
                {
                    Date = table.Column<LocalDate>(type: "date", nullable: false),
                    TemplateName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    SentCount = table.Column<int>(type: "integer", nullable: false),
                    FailedCount = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_email_daily_send_counts", x => new { x.Date, x.TemplateName });
                });

            migrationBuilder.CreateIndex(
                name: "IX_email_daily_send_counts_Date",
                table: "email_daily_send_counts",
                column: "Date");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "email_daily_send_counts");
        }
    }
}
