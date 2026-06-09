using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace Humans.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddSurveySection : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "survey_invitations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SurveyId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    SentAt = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    LatestEmailStatus = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    ReminderSentAt = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    Completed = table.Column<bool>(type: "boolean", nullable: false),
                    Started = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_survey_invitations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "surveys",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Title = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'{}'::jsonb"),
                    Intro = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'{}'::jsonb"),
                    ThankYou = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'{}'::jsonb"),
                    DefaultCulture = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    AllowAnonymous = table.Column<bool>(type: "boolean", nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    OpensAt = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    ClosesAt = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    AudienceType = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    AudienceTeamId = table.Column<Guid>(type: "uuid", nullable: true),
                    PublicSlug = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    PublicStartedCount = table.Column<int>(type: "integer", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_surveys", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "survey_responses",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SurveyId = table.Column<Guid>(type: "uuid", nullable: false),
                    InvitationId = table.Column<Guid>(type: "uuid", nullable: true),
                    UserId = table.Column<Guid>(type: "uuid", nullable: true),
                    Anonymity = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    InputMethod = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Culture = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    SubmittedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_survey_responses", x => x.Id);
                    table.ForeignKey(
                        name: "FK_survey_responses_survey_invitations_InvitationId",
                        column: x => x.InvitationId,
                        principalTable: "survey_invitations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "survey_questions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SurveyId = table.Column<Guid>(type: "uuid", nullable: false),
                    PageNumber = table.Column<int>(type: "integer", nullable: false),
                    Order = table.Column<int>(type: "integer", nullable: false),
                    Type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Prompt = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'{}'::jsonb"),
                    HelpText = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'{}'::jsonb"),
                    IsRequired = table.Column<bool>(type: "boolean", nullable: false),
                    RatingMin = table.Column<int>(type: "integer", nullable: true),
                    RatingMax = table.Column<int>(type: "integer", nullable: true),
                    RatingMinLabel = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'{}'::jsonb"),
                    RatingMaxLabel = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'{}'::jsonb"),
                    ShowIf = table.Column<string>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_survey_questions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_survey_questions_surveys_SurveyId",
                        column: x => x.SurveyId,
                        principalTable: "surveys",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "survey_answers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ResponseId = table.Column<Guid>(type: "uuid", nullable: false),
                    QuestionId = table.Column<Guid>(type: "uuid", nullable: false),
                    SelectedOptionValues = table.Column<string>(type: "jsonb", nullable: false),
                    TextValue = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    RatingValue = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_survey_answers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_survey_answers_survey_questions_QuestionId",
                        column: x => x.QuestionId,
                        principalTable: "survey_questions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_survey_answers_survey_responses_ResponseId",
                        column: x => x.ResponseId,
                        principalTable: "survey_responses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "survey_question_options",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    QuestionId = table.Column<Guid>(type: "uuid", nullable: false),
                    Order = table.Column<int>(type: "integer", nullable: false),
                    Value = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Label = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'{}'::jsonb")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_survey_question_options", x => x.Id);
                    table.ForeignKey(
                        name: "FK_survey_question_options_survey_questions_QuestionId",
                        column: x => x.QuestionId,
                        principalTable: "survey_questions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_survey_answers_QuestionId",
                table: "survey_answers",
                column: "QuestionId");

            migrationBuilder.CreateIndex(
                name: "IX_survey_answers_ResponseId",
                table: "survey_answers",
                column: "ResponseId");

            migrationBuilder.CreateIndex(
                name: "IX_survey_invitations_SurveyId_Completed_SentAt",
                table: "survey_invitations",
                columns: new[] { "SurveyId", "Completed", "SentAt" });

            migrationBuilder.CreateIndex(
                name: "IX_survey_invitations_SurveyId_UserId",
                table: "survey_invitations",
                columns: new[] { "SurveyId", "UserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_survey_question_options_QuestionId_Order",
                table: "survey_question_options",
                columns: new[] { "QuestionId", "Order" });

            migrationBuilder.CreateIndex(
                name: "IX_survey_questions_SurveyId_PageNumber_Order",
                table: "survey_questions",
                columns: new[] { "SurveyId", "PageNumber", "Order" });

            migrationBuilder.CreateIndex(
                name: "IX_survey_responses_InvitationId",
                table: "survey_responses",
                column: "InvitationId");

            migrationBuilder.CreateIndex(
                name: "IX_survey_responses_SurveyId",
                table: "survey_responses",
                column: "SurveyId");

            migrationBuilder.CreateIndex(
                name: "IX_survey_responses_SurveyId_UserId",
                table: "survey_responses",
                columns: new[] { "SurveyId", "UserId" });

            migrationBuilder.CreateIndex(
                name: "IX_surveys_PublicSlug",
                table: "surveys",
                column: "PublicSlug",
                unique: true,
                filter: "\"PublicSlug\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_surveys_Status",
                table: "surveys",
                column: "Status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "survey_answers");

            migrationBuilder.DropTable(
                name: "survey_question_options");

            migrationBuilder.DropTable(
                name: "survey_responses");

            migrationBuilder.DropTable(
                name: "survey_questions");

            migrationBuilder.DropTable(
                name: "survey_invitations");

            migrationBuilder.DropTable(
                name: "surveys");
        }
    }
}
