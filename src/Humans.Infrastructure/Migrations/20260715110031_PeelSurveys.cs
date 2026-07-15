using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace Humans.Infrastructure.Migrations
{
    /// <summary>
    /// Snapshot-only migration for the Surveys peel
    /// (nobodies-collective/Humans#858): <c>surveys</c>, <c>survey_questions</c>,
    /// <c>survey_question_options</c>, <c>survey_invitations</c>,
    /// <c>survey_responses</c> and <c>survey_answers</c> moved to
    /// <c>SurveysDbContext</c>, which owns the physical tables from here on. The
    /// scaffolded <c>DropTable</c>/<c>CreateTable</c> bodies were deliberately
    /// emptied — Peter-authorized per-instance exception to
    /// <c>memory/architecture/no-hand-edited-migrations.md</c> in the #858
    /// execution brief — so applying this migration changes no physical schema.
    /// </summary>
    public partial class PeelSurveys : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
