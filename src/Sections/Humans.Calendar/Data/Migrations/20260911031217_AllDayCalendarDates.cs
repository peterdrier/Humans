using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace Humans.Calendar.Data.Migrations
{
    /// <inheritdoc />
    public partial class AllDayCalendarDates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<Instant>(
                name: "StartUtc",
                table: "calendar_events",
                type: "timestamp with time zone",
                nullable: true,
                oldClrType: typeof(Instant),
                oldType: "timestamp with time zone");

            migrationBuilder.AddColumn<LocalDate>(
                name: "EndDateExclusive",
                table: "calendar_events",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<LocalDate>(
                name: "RecurrenceUntilDate",
                table: "calendar_events",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<LocalDate>(
                name: "StartDate",
                table: "calendar_events",
                type: "date",
                nullable: true);

            migrationBuilder.AlterColumn<Instant>(
                name: "OriginalOccurrenceStartUtc",
                table: "calendar_event_exceptions",
                type: "timestamp with time zone",
                nullable: true,
                oldClrType: typeof(Instant),
                oldType: "timestamp with time zone");

            migrationBuilder.AddColumn<LocalDate>(
                name: "OriginalOccurrenceDate",
                table: "calendar_event_exceptions",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<LocalDate>(
                name: "OverrideEndDateExclusive",
                table: "calendar_event_exceptions",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<LocalDate>(
                name: "OverrideStartDate",
                table: "calendar_event_exceptions",
                type: "date",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EndDateExclusive",
                table: "calendar_events");

            migrationBuilder.DropColumn(
                name: "RecurrenceUntilDate",
                table: "calendar_events");

            migrationBuilder.DropColumn(
                name: "StartDate",
                table: "calendar_events");

            migrationBuilder.DropColumn(
                name: "OriginalOccurrenceDate",
                table: "calendar_event_exceptions");

            migrationBuilder.DropColumn(
                name: "OverrideEndDateExclusive",
                table: "calendar_event_exceptions");

            migrationBuilder.DropColumn(
                name: "OverrideStartDate",
                table: "calendar_event_exceptions");

            migrationBuilder.AlterColumn<Instant>(
                name: "StartUtc",
                table: "calendar_events",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: NodaTime.Instant.FromUnixTimeTicks(0L),
                oldClrType: typeof(Instant),
                oldType: "timestamp with time zone",
                oldNullable: true);

            migrationBuilder.AlterColumn<Instant>(
                name: "OriginalOccurrenceStartUtc",
                table: "calendar_event_exceptions",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: NodaTime.Instant.FromUnixTimeTicks(0L),
                oldClrType: typeof(Instant),
                oldType: "timestamp with time zone",
                oldNullable: true);
        }
    }
}
