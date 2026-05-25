using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Humans.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class MoveDietaryMedicalToProfile : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Allergies",
                table: "profiles",
                type: "jsonb",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.AddColumn<string>(
                name: "AllergyOtherText",
                table: "profiles",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DietaryPreference",
                table: "profiles",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "IntoleranceOtherText",
                table: "profiles",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Intolerances",
                table: "profiles",
                type: "jsonb",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.AddColumn<string>(
                name: "MedicalConditions",
                table: "profiles",
                type: "character varying(4000)",
                maxLength: 4000,
                nullable: true);

            // Backfill the new Profile columns from the existing VolunteerEventProfile
            // rows (1:1 by UserId). ~500 rows. The VEP columns are intentionally
            // RETAINED (not dropped) until a post-prod-soak follow-up per
            // memory/architecture/no-drops-until-prod-verified.md.
            migrationBuilder.Sql(@"
                UPDATE profiles p SET
                    ""DietaryPreference""    = v.""DietaryPreference"",
                    ""Allergies""            = v.""Allergies"",
                    ""AllergyOtherText""     = v.""AllergyOtherText"",
                    ""Intolerances""         = v.""Intolerances"",
                    ""IntoleranceOtherText"" = v.""IntoleranceOtherText"",
                    ""MedicalConditions""    = v.""MedicalConditions""
                FROM volunteer_event_profiles v
                WHERE v.""UserId"" = p.""UserId"";");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Allergies",
                table: "profiles");

            migrationBuilder.DropColumn(
                name: "AllergyOtherText",
                table: "profiles");

            migrationBuilder.DropColumn(
                name: "DietaryPreference",
                table: "profiles");

            migrationBuilder.DropColumn(
                name: "IntoleranceOtherText",
                table: "profiles");

            migrationBuilder.DropColumn(
                name: "Intolerances",
                table: "profiles");

            migrationBuilder.DropColumn(
                name: "MedicalConditions",
                table: "profiles");
        }
    }
}
