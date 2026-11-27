using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Humans.Shifts.Data.Migrations
{
    /// <inheritdoc />
    public partial class DropVolunteerProfileDietaryMedicalColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Allergies",
                table: "shift_volunteer_event_profiles");

            migrationBuilder.DropColumn(
                name: "AllergyOtherText",
                table: "shift_volunteer_event_profiles");

            migrationBuilder.DropColumn(
                name: "DietaryPreference",
                table: "shift_volunteer_event_profiles");

            migrationBuilder.DropColumn(
                name: "IntoleranceOtherText",
                table: "shift_volunteer_event_profiles");

            migrationBuilder.DropColumn(
                name: "Intolerances",
                table: "shift_volunteer_event_profiles");

            migrationBuilder.DropColumn(
                name: "MedicalConditions",
                table: "shift_volunteer_event_profiles");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Allergies",
                table: "shift_volunteer_event_profiles",
                type: "jsonb",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "AllergyOtherText",
                table: "shift_volunteer_event_profiles",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DietaryPreference",
                table: "shift_volunteer_event_profiles",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "IntoleranceOtherText",
                table: "shift_volunteer_event_profiles",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Intolerances",
                table: "shift_volunteer_event_profiles",
                type: "jsonb",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "MedicalConditions",
                table: "shift_volunteer_event_profiles",
                type: "character varying(4000)",
                maxLength: 4000,
                nullable: true);
        }
    }
}
