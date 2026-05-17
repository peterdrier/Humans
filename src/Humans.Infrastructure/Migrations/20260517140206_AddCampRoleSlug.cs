using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Humans.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddCampRoleSlug : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Slug",
                table: "camp_role_definitions",
                type: "character varying(60)",
                maxLength: 60,
                nullable: false,
                defaultValue: "");

            // Backfill Slug from Name using the same kebab-case algorithm as
            // Humans.Application.Helpers.SlugHelper.GenerateSlug. For rows whose
            // names slug-collide we keep the first row's clean slug and append a
            // short id suffix on duplicates so the unique index can be created.
            // Administrators can edit later via the role form.
            // Issue nobodies-collective/Humans#740.
            migrationBuilder.Sql("""
                WITH base AS (
                    SELECT
                        "Id",
                        CASE
                            WHEN trim(both '-' from regexp_replace(regexp_replace(lower("Name"), '[^a-z0-9-]+', '-', 'g'), '-{2,}', '-', 'g')) = ''
                            THEN 'role-' || substr("Id"::text, 1, 8)
                            ELSE trim(both '-' from regexp_replace(regexp_replace(lower("Name"), '[^a-z0-9-]+', '-', 'g'), '-{2,}', '-', 'g'))
                        END AS slug_base,
                        row_number() OVER (
                            PARTITION BY trim(both '-' from regexp_replace(regexp_replace(lower("Name"), '[^a-z0-9-]+', '-', 'g'), '-{2,}', '-', 'g'))
                            ORDER BY "CreatedAt", "Id"
                        ) AS rn
                    FROM camp_role_definitions
                )
                UPDATE camp_role_definitions d
                SET "Slug" = CASE
                    WHEN b.rn = 1 THEN b.slug_base
                    ELSE b.slug_base || '-' || substr(d."Id"::text, 1, 6)
                END
                FROM base b
                WHERE d."Id" = b."Id"
                """);

            migrationBuilder.CreateIndex(
                name: "IX_camp_role_definitions_slug_unique",
                table: "camp_role_definitions",
                column: "Slug",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_camp_role_definitions_slug_unique",
                table: "camp_role_definitions");

            migrationBuilder.DropColumn(
                name: "Slug",
                table: "camp_role_definitions");
        }
    }
}
