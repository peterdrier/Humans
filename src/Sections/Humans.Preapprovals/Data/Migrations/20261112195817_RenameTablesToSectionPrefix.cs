using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Humans.Preapprovals.Data.Migrations
{
    /// <inheritdoc />
    public partial class RenameTablesToSectionPrefix : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_pre_grants_pre_programs_ProgramId",
                table: "pre_grants");

            migrationBuilder.DropForeignKey(
                name: "FK_pre_program_managers_pre_programs_ProgramId",
                table: "pre_program_managers");

            migrationBuilder.DropForeignKey(
                name: "FK_pre_reminders_sent_pre_grants_GrantId",
                table: "pre_reminders_sent");

            migrationBuilder.DropPrimaryKey(
                name: "PK_pre_reminders_sent",
                table: "pre_reminders_sent");

            migrationBuilder.DropPrimaryKey(
                name: "PK_pre_programs",
                table: "pre_programs");

            migrationBuilder.DropPrimaryKey(
                name: "PK_pre_program_managers",
                table: "pre_program_managers");

            migrationBuilder.DropPrimaryKey(
                name: "PK_pre_grants",
                table: "pre_grants");

            migrationBuilder.RenameTable(
                name: "pre_reminders_sent",
                newName: "preapproval_sent_reminders");

            migrationBuilder.RenameTable(
                name: "pre_programs",
                newName: "preapproval_programs");

            migrationBuilder.RenameTable(
                name: "pre_program_managers",
                newName: "preapproval_program_managers");

            migrationBuilder.RenameTable(
                name: "pre_grants",
                newName: "preapproval_grants");

            migrationBuilder.RenameIndex(
                name: "IX_pre_programs_Status",
                table: "preapproval_programs",
                newName: "IX_preapproval_programs_Status");

            migrationBuilder.RenameIndex(
                name: "IX_pre_program_managers_UserId",
                table: "preapproval_program_managers",
                newName: "IX_preapproval_program_managers_UserId");

            migrationBuilder.RenameIndex(
                name: "IX_pre_grants_ProgramId_Status",
                table: "preapproval_grants",
                newName: "IX_preapproval_grants_ProgramId_Status");

            migrationBuilder.RenameIndex(
                name: "IX_pre_grants_ExpenseReportId",
                table: "preapproval_grants",
                newName: "IX_preapproval_grants_ExpenseReportId");

            migrationBuilder.RenameIndex(
                name: "IX_pre_grants_BeneficiaryUserId",
                table: "preapproval_grants",
                newName: "IX_preapproval_grants_BeneficiaryUserId");

            migrationBuilder.AddPrimaryKey(
                name: "PK_preapproval_sent_reminders",
                table: "preapproval_sent_reminders",
                columns: new[] { "GrantId", "Kind", "ClaimBy" });

            migrationBuilder.AddPrimaryKey(
                name: "PK_preapproval_programs",
                table: "preapproval_programs",
                column: "Id");

            migrationBuilder.AddPrimaryKey(
                name: "PK_preapproval_program_managers",
                table: "preapproval_program_managers",
                columns: new[] { "ProgramId", "UserId" });

            migrationBuilder.AddPrimaryKey(
                name: "PK_preapproval_grants",
                table: "preapproval_grants",
                column: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_preapproval_grants_preapproval_programs_ProgramId",
                table: "preapproval_grants",
                column: "ProgramId",
                principalTable: "preapproval_programs",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_preapproval_program_managers_preapproval_programs_ProgramId",
                table: "preapproval_program_managers",
                column: "ProgramId",
                principalTable: "preapproval_programs",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_preapproval_sent_reminders_preapproval_grants_GrantId",
                table: "preapproval_sent_reminders",
                column: "GrantId",
                principalTable: "preapproval_grants",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_preapproval_grants_preapproval_programs_ProgramId",
                table: "preapproval_grants");

            migrationBuilder.DropForeignKey(
                name: "FK_preapproval_program_managers_preapproval_programs_ProgramId",
                table: "preapproval_program_managers");

            migrationBuilder.DropForeignKey(
                name: "FK_preapproval_sent_reminders_preapproval_grants_GrantId",
                table: "preapproval_sent_reminders");

            migrationBuilder.DropPrimaryKey(
                name: "PK_preapproval_sent_reminders",
                table: "preapproval_sent_reminders");

            migrationBuilder.DropPrimaryKey(
                name: "PK_preapproval_programs",
                table: "preapproval_programs");

            migrationBuilder.DropPrimaryKey(
                name: "PK_preapproval_program_managers",
                table: "preapproval_program_managers");

            migrationBuilder.DropPrimaryKey(
                name: "PK_preapproval_grants",
                table: "preapproval_grants");

            migrationBuilder.RenameTable(
                name: "preapproval_sent_reminders",
                newName: "pre_reminders_sent");

            migrationBuilder.RenameTable(
                name: "preapproval_programs",
                newName: "pre_programs");

            migrationBuilder.RenameTable(
                name: "preapproval_program_managers",
                newName: "pre_program_managers");

            migrationBuilder.RenameTable(
                name: "preapproval_grants",
                newName: "pre_grants");

            migrationBuilder.RenameIndex(
                name: "IX_preapproval_programs_Status",
                table: "pre_programs",
                newName: "IX_pre_programs_Status");

            migrationBuilder.RenameIndex(
                name: "IX_preapproval_program_managers_UserId",
                table: "pre_program_managers",
                newName: "IX_pre_program_managers_UserId");

            migrationBuilder.RenameIndex(
                name: "IX_preapproval_grants_ProgramId_Status",
                table: "pre_grants",
                newName: "IX_pre_grants_ProgramId_Status");

            migrationBuilder.RenameIndex(
                name: "IX_preapproval_grants_ExpenseReportId",
                table: "pre_grants",
                newName: "IX_pre_grants_ExpenseReportId");

            migrationBuilder.RenameIndex(
                name: "IX_preapproval_grants_BeneficiaryUserId",
                table: "pre_grants",
                newName: "IX_pre_grants_BeneficiaryUserId");

            migrationBuilder.AddPrimaryKey(
                name: "PK_pre_reminders_sent",
                table: "pre_reminders_sent",
                columns: new[] { "GrantId", "Kind", "ClaimBy" });

            migrationBuilder.AddPrimaryKey(
                name: "PK_pre_programs",
                table: "pre_programs",
                column: "Id");

            migrationBuilder.AddPrimaryKey(
                name: "PK_pre_program_managers",
                table: "pre_program_managers",
                columns: new[] { "ProgramId", "UserId" });

            migrationBuilder.AddPrimaryKey(
                name: "PK_pre_grants",
                table: "pre_grants",
                column: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_pre_grants_pre_programs_ProgramId",
                table: "pre_grants",
                column: "ProgramId",
                principalTable: "pre_programs",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_pre_program_managers_pre_programs_ProgramId",
                table: "pre_program_managers",
                column: "ProgramId",
                principalTable: "pre_programs",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_pre_reminders_sent_pre_grants_GrantId",
                table: "pre_reminders_sent",
                column: "GrantId",
                principalTable: "pre_grants",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
