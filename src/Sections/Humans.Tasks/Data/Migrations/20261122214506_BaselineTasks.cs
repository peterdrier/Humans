using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace Humans.Tasks.Data.Migrations
{
    /// <inheritdoc />
    public partial class BaselineTasks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "task_items",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerKind = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    OwnerId = table.Column<Guid>(type: "uuid", nullable: false),
                    Title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    EventId = table.Column<Guid>(type: "uuid", nullable: true),
                    StartDate = table.Column<LocalDate>(type: "date", nullable: true),
                    StartDayOffset = table.Column<int>(type: "integer", nullable: true),
                    DueDate = table.Column<LocalDate>(type: "date", nullable: true),
                    DueDayOffset = table.Column<int>(type: "integer", nullable: true),
                    Status = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    BlockedNote = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    Priority = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    SlotCount = table.Column<int>(type: "integer", nullable: false),
                    NeedsApproval = table.Column<bool>(type: "boolean", nullable: false),
                    ApprovalReason = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    IsHidden = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_task_items", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "task_assignments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TaskItemId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    State = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    CreatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_task_assignments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_task_assignments_task_items_TaskItemId",
                        column: x => x.TaskItemId,
                        principalTable: "task_items",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "task_dependencies",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TaskItemId = table.Column<Guid>(type: "uuid", nullable: false),
                    DependsOnTaskItemId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_task_dependencies", x => x.Id);
                    table.ForeignKey(
                        name: "FK_task_dependencies_task_items_DependsOnTaskItemId",
                        column: x => x.DependsOnTaskItemId,
                        principalTable: "task_items",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_task_dependencies_task_items_TaskItemId",
                        column: x => x.TaskItemId,
                        principalTable: "task_items",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_task_assignments_TaskItemId_UserId",
                table: "task_assignments",
                columns: new[] { "TaskItemId", "UserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_task_assignments_UserId",
                table: "task_assignments",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_task_dependencies_DependsOnTaskItemId",
                table: "task_dependencies",
                column: "DependsOnTaskItemId");

            migrationBuilder.CreateIndex(
                name: "IX_task_dependencies_TaskItemId_DependsOnTaskItemId",
                table: "task_dependencies",
                columns: new[] { "TaskItemId", "DependsOnTaskItemId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_task_items_EventId",
                table: "task_items",
                column: "EventId");

            migrationBuilder.CreateIndex(
                name: "IX_task_items_OwnerKind_OwnerId",
                table: "task_items",
                columns: new[] { "OwnerKind", "OwnerId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "task_assignments");

            migrationBuilder.DropTable(
                name: "task_dependencies");

            migrationBuilder.DropTable(
                name: "task_items");
        }
    }
}
