using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace Humans.Workgroups.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddWorkgroups : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "workgroups",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Slug = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Purpose = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    Deliverable = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    DeliverableKind = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Audience = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    TargetDate = table.Column<LocalDate>(type: "date", nullable: true),
                    Status = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    DormantReason = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    DriveFolderId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    DiscordChannelUrl = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    Reasons = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    AppliedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    AppliedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    RegisteredAt = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    EndedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    DormantSince = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_workgroups", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "workgroup_documents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkgroupId = table.Column<Guid>(type: "uuid", nullable: false),
                    Title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Kind = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Body = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    CommentCategories = table.Column<string>(type: "jsonb", nullable: false),
                    CommentsOpenAt = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    CommentsCloseAt = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    DeliveredAt = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    Disposition = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    DispositionNote = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    DispositionAt = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    DispositionByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    UpdatedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_workgroup_documents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_workgroup_documents_workgroups_WorkgroupId",
                        column: x => x.WorkgroupId,
                        principalTable: "workgroups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "workgroup_meetings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkgroupId = table.Column<Guid>(type: "uuid", nullable: false),
                    Title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    StartUtc = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    EndUtc = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    Location = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    LocationUrl = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    IsPublic = table.Column<bool>(type: "boolean", nullable: false),
                    Minutes = table.Column<string>(type: "text", nullable: true),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    DeletedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_workgroup_meetings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_workgroup_meetings_workgroups_WorkgroupId",
                        column: x => x.WorkgroupId,
                        principalTable: "workgroups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "workgroup_members",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkgroupId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Role = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    JoinedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    LeftAt = table.Column<Instant>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_workgroup_members", x => x.Id);
                    table.ForeignKey(
                        name: "FK_workgroup_members_workgroups_WorkgroupId",
                        column: x => x.WorkgroupId,
                        principalTable: "workgroups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "workgroup_document_comments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DocumentId = table.Column<Guid>(type: "uuid", nullable: false),
                    Category = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    AuthorUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    Body = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    CreatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    Disposition = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Response = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    RespondedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    RespondedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    HiddenAt = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    HiddenByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    HiddenReason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_workgroup_document_comments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_workgroup_document_comments_workgroup_documents_DocumentId",
                        column: x => x.DocumentId,
                        principalTable: "workgroup_documents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "workgroup_log_entries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkgroupId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    OccurredOn = table.Column<LocalDate>(type: "date", nullable: false),
                    Title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Body = table.Column<string>(type: "character varying(16000)", maxLength: 16000, nullable: true),
                    AuthorUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    DocumentId = table.Column<Guid>(type: "uuid", nullable: true),
                    SurveyId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_workgroup_log_entries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_workgroup_log_entries_workgroup_documents_DocumentId",
                        column: x => x.DocumentId,
                        principalTable: "workgroup_documents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_workgroup_log_entries_workgroups_WorkgroupId",
                        column: x => x.WorkgroupId,
                        principalTable: "workgroups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_workgroup_document_comments_AuthorUserId",
                table: "workgroup_document_comments",
                column: "AuthorUserId",
                filter: "\"AuthorUserId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_workgroup_document_comments_DocumentId_Category",
                table: "workgroup_document_comments",
                columns: new[] { "DocumentId", "Category" });

            migrationBuilder.CreateIndex(
                name: "IX_workgroup_documents_CreatedByUserId",
                table: "workgroup_documents",
                column: "CreatedByUserId",
                filter: "\"CreatedByUserId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_workgroup_documents_WorkgroupId_Status",
                table: "workgroup_documents",
                columns: new[] { "WorkgroupId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_workgroup_log_entries_AuthorUserId",
                table: "workgroup_log_entries",
                column: "AuthorUserId",
                filter: "\"AuthorUserId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_workgroup_log_entries_DocumentId",
                table: "workgroup_log_entries",
                column: "DocumentId");

            migrationBuilder.CreateIndex(
                name: "IX_workgroup_log_entries_WorkgroupId_OccurredOn",
                table: "workgroup_log_entries",
                columns: new[] { "WorkgroupId", "OccurredOn" });

            migrationBuilder.CreateIndex(
                name: "IX_workgroup_meetings_CreatedByUserId",
                table: "workgroup_meetings",
                column: "CreatedByUserId",
                filter: "\"CreatedByUserId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_workgroup_meetings_WorkgroupId_StartUtc",
                table: "workgroup_meetings",
                columns: new[] { "WorkgroupId", "StartUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_workgroup_members_UserId",
                table: "workgroup_members",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_workgroup_members_WorkgroupId_UserId",
                table: "workgroup_members",
                columns: new[] { "WorkgroupId", "UserId" },
                unique: true,
                filter: "\"LeftAt\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_workgroups_DriveFolderId",
                table: "workgroups",
                column: "DriveFolderId",
                unique: true,
                filter: "\"DriveFolderId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_workgroups_Slug",
                table: "workgroups",
                column: "Slug");

            migrationBuilder.CreateIndex(
                name: "IX_workgroups_Status",
                table: "workgroups",
                column: "Status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "workgroup_document_comments");

            migrationBuilder.DropTable(
                name: "workgroup_log_entries");

            migrationBuilder.DropTable(
                name: "workgroup_meetings");

            migrationBuilder.DropTable(
                name: "workgroup_members");

            migrationBuilder.DropTable(
                name: "workgroup_documents");

            migrationBuilder.DropTable(
                name: "workgroups");
        }
    }
}
