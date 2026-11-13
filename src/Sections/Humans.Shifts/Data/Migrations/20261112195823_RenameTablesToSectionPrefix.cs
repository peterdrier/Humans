using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Humans.Shifts.Data.Migrations
{
    /// <inheritdoc />
    public partial class RenameTablesToSectionPrefix : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_general_availability_event_settings_EventSettingsId",
                table: "general_availability");

            migrationBuilder.DropForeignKey(
                name: "FK_rota_shift_tags_rotas_RotaId",
                table: "rota_shift_tags");

            migrationBuilder.DropForeignKey(
                name: "FK_rota_shift_tags_shift_tags_ShiftTagId",
                table: "rota_shift_tags");

            migrationBuilder.DropForeignKey(
                name: "FK_rotas_event_settings_EventSettingsId",
                table: "rotas");

            migrationBuilder.DropForeignKey(
                name: "FK_shifts_rotas_RotaId",
                table: "shifts");

            migrationBuilder.DropForeignKey(
                name: "FK_volunteer_build_statuses_event_settings_EventSettingsId",
                table: "volunteer_build_statuses");

            migrationBuilder.DropForeignKey(
                name: "FK_volunteer_tag_preferences_shift_tags_ShiftTagId",
                table: "volunteer_tag_preferences");

            migrationBuilder.DropPrimaryKey(
                name: "PK_volunteer_tag_preferences",
                table: "volunteer_tag_preferences");

            migrationBuilder.DropPrimaryKey(
                name: "PK_volunteer_event_profiles",
                table: "volunteer_event_profiles");

            migrationBuilder.DropPrimaryKey(
                name: "PK_volunteer_build_statuses",
                table: "volunteer_build_statuses");

            migrationBuilder.DropPrimaryKey(
                name: "PK_rotas",
                table: "rotas");

            migrationBuilder.DropPrimaryKey(
                name: "PK_rota_shift_tags",
                table: "rota_shift_tags");

            migrationBuilder.DropPrimaryKey(
                name: "PK_general_availability",
                table: "general_availability");

            migrationBuilder.DropPrimaryKey(
                name: "PK_event_settings",
                table: "event_settings");

            migrationBuilder.RenameTable(
                name: "volunteer_tag_preferences",
                newName: "shift_volunteer_tag_preferences");

            migrationBuilder.RenameTable(
                name: "volunteer_event_profiles",
                newName: "shift_volunteer_event_profiles");

            migrationBuilder.RenameTable(
                name: "volunteer_build_statuses",
                newName: "shift_volunteer_build_statuses");

            migrationBuilder.RenameTable(
                name: "rotas",
                newName: "shift_rotas");

            migrationBuilder.RenameTable(
                name: "rota_shift_tags",
                newName: "shift_rota_tags");

            migrationBuilder.RenameTable(
                name: "general_availability",
                newName: "shift_general_availabilities");

            migrationBuilder.RenameTable(
                name: "event_settings",
                newName: "shift_event_settings");

            migrationBuilder.RenameIndex(
                name: "IX_volunteer_tag_preferences_UserId",
                table: "shift_volunteer_tag_preferences",
                newName: "IX_shift_volunteer_tag_preferences_UserId");

            migrationBuilder.RenameIndex(
                name: "IX_volunteer_tag_preferences_ShiftTagId",
                table: "shift_volunteer_tag_preferences",
                newName: "IX_shift_volunteer_tag_preferences_ShiftTagId");

            migrationBuilder.RenameIndex(
                name: "IX_volunteer_event_profiles_UserId",
                table: "shift_volunteer_event_profiles",
                newName: "IX_shift_volunteer_event_profiles_UserId");

            migrationBuilder.RenameIndex(
                name: "IX_volunteer_build_statuses_UserId_EventSettingsId",
                table: "shift_volunteer_build_statuses",
                newName: "IX_shift_volunteer_build_statuses_UserId_EventSettingsId");

            migrationBuilder.RenameIndex(
                name: "IX_volunteer_build_statuses_EventSettingsId",
                table: "shift_volunteer_build_statuses",
                newName: "IX_shift_volunteer_build_statuses_EventSettingsId");

            migrationBuilder.RenameIndex(
                name: "IX_rotas_EventSettingsId_TeamId",
                table: "shift_rotas",
                newName: "IX_shift_rotas_EventSettingsId_TeamId");

            migrationBuilder.RenameIndex(
                name: "IX_rota_shift_tags_ShiftTagId",
                table: "shift_rota_tags",
                newName: "IX_shift_rota_tags_ShiftTagId");

            migrationBuilder.RenameIndex(
                name: "IX_general_availability_UserId_EventSettingsId",
                table: "shift_general_availabilities",
                newName: "IX_shift_general_availabilities_UserId_EventSettingsId");

            migrationBuilder.RenameIndex(
                name: "IX_general_availability_EventSettingsId",
                table: "shift_general_availabilities",
                newName: "IX_shift_general_availabilities_EventSettingsId");

            migrationBuilder.RenameIndex(
                name: "IX_event_settings_IsActive",
                table: "shift_event_settings",
                newName: "IX_shift_event_settings_IsActive");

            migrationBuilder.AddPrimaryKey(
                name: "PK_shift_volunteer_tag_preferences",
                table: "shift_volunteer_tag_preferences",
                column: "Id");

            migrationBuilder.AddPrimaryKey(
                name: "PK_shift_volunteer_event_profiles",
                table: "shift_volunteer_event_profiles",
                column: "Id");

            migrationBuilder.AddPrimaryKey(
                name: "PK_shift_volunteer_build_statuses",
                table: "shift_volunteer_build_statuses",
                column: "Id");

            migrationBuilder.AddPrimaryKey(
                name: "PK_shift_rotas",
                table: "shift_rotas",
                column: "Id");

            migrationBuilder.AddPrimaryKey(
                name: "PK_shift_rota_tags",
                table: "shift_rota_tags",
                columns: new[] { "RotaId", "ShiftTagId" });

            migrationBuilder.AddPrimaryKey(
                name: "PK_shift_general_availabilities",
                table: "shift_general_availabilities",
                column: "Id");

            migrationBuilder.AddPrimaryKey(
                name: "PK_shift_event_settings",
                table: "shift_event_settings",
                column: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_shift_general_availabilities_shift_event_settings_EventSett~",
                table: "shift_general_availabilities",
                column: "EventSettingsId",
                principalTable: "shift_event_settings",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_shift_rota_tags_shift_rotas_RotaId",
                table: "shift_rota_tags",
                column: "RotaId",
                principalTable: "shift_rotas",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_shift_rota_tags_shift_tags_ShiftTagId",
                table: "shift_rota_tags",
                column: "ShiftTagId",
                principalTable: "shift_tags",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_shift_rotas_shift_event_settings_EventSettingsId",
                table: "shift_rotas",
                column: "EventSettingsId",
                principalTable: "shift_event_settings",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_shift_volunteer_build_statuses_shift_event_settings_EventSe~",
                table: "shift_volunteer_build_statuses",
                column: "EventSettingsId",
                principalTable: "shift_event_settings",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_shift_volunteer_tag_preferences_shift_tags_ShiftTagId",
                table: "shift_volunteer_tag_preferences",
                column: "ShiftTagId",
                principalTable: "shift_tags",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_shifts_shift_rotas_RotaId",
                table: "shifts",
                column: "RotaId",
                principalTable: "shift_rotas",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_shift_general_availabilities_shift_event_settings_EventSett~",
                table: "shift_general_availabilities");

            migrationBuilder.DropForeignKey(
                name: "FK_shift_rota_tags_shift_rotas_RotaId",
                table: "shift_rota_tags");

            migrationBuilder.DropForeignKey(
                name: "FK_shift_rota_tags_shift_tags_ShiftTagId",
                table: "shift_rota_tags");

            migrationBuilder.DropForeignKey(
                name: "FK_shift_rotas_shift_event_settings_EventSettingsId",
                table: "shift_rotas");

            migrationBuilder.DropForeignKey(
                name: "FK_shift_volunteer_build_statuses_shift_event_settings_EventSe~",
                table: "shift_volunteer_build_statuses");

            migrationBuilder.DropForeignKey(
                name: "FK_shift_volunteer_tag_preferences_shift_tags_ShiftTagId",
                table: "shift_volunteer_tag_preferences");

            migrationBuilder.DropForeignKey(
                name: "FK_shifts_shift_rotas_RotaId",
                table: "shifts");

            migrationBuilder.DropPrimaryKey(
                name: "PK_shift_volunteer_tag_preferences",
                table: "shift_volunteer_tag_preferences");

            migrationBuilder.DropPrimaryKey(
                name: "PK_shift_volunteer_event_profiles",
                table: "shift_volunteer_event_profiles");

            migrationBuilder.DropPrimaryKey(
                name: "PK_shift_volunteer_build_statuses",
                table: "shift_volunteer_build_statuses");

            migrationBuilder.DropPrimaryKey(
                name: "PK_shift_rotas",
                table: "shift_rotas");

            migrationBuilder.DropPrimaryKey(
                name: "PK_shift_rota_tags",
                table: "shift_rota_tags");

            migrationBuilder.DropPrimaryKey(
                name: "PK_shift_general_availabilities",
                table: "shift_general_availabilities");

            migrationBuilder.DropPrimaryKey(
                name: "PK_shift_event_settings",
                table: "shift_event_settings");

            migrationBuilder.RenameTable(
                name: "shift_volunteer_tag_preferences",
                newName: "volunteer_tag_preferences");

            migrationBuilder.RenameTable(
                name: "shift_volunteer_event_profiles",
                newName: "volunteer_event_profiles");

            migrationBuilder.RenameTable(
                name: "shift_volunteer_build_statuses",
                newName: "volunteer_build_statuses");

            migrationBuilder.RenameTable(
                name: "shift_rotas",
                newName: "rotas");

            migrationBuilder.RenameTable(
                name: "shift_rota_tags",
                newName: "rota_shift_tags");

            migrationBuilder.RenameTable(
                name: "shift_general_availabilities",
                newName: "general_availability");

            migrationBuilder.RenameTable(
                name: "shift_event_settings",
                newName: "event_settings");

            migrationBuilder.RenameIndex(
                name: "IX_shift_volunteer_tag_preferences_UserId",
                table: "volunteer_tag_preferences",
                newName: "IX_volunteer_tag_preferences_UserId");

            migrationBuilder.RenameIndex(
                name: "IX_shift_volunteer_tag_preferences_ShiftTagId",
                table: "volunteer_tag_preferences",
                newName: "IX_volunteer_tag_preferences_ShiftTagId");

            migrationBuilder.RenameIndex(
                name: "IX_shift_volunteer_event_profiles_UserId",
                table: "volunteer_event_profiles",
                newName: "IX_volunteer_event_profiles_UserId");

            migrationBuilder.RenameIndex(
                name: "IX_shift_volunteer_build_statuses_UserId_EventSettingsId",
                table: "volunteer_build_statuses",
                newName: "IX_volunteer_build_statuses_UserId_EventSettingsId");

            migrationBuilder.RenameIndex(
                name: "IX_shift_volunteer_build_statuses_EventSettingsId",
                table: "volunteer_build_statuses",
                newName: "IX_volunteer_build_statuses_EventSettingsId");

            migrationBuilder.RenameIndex(
                name: "IX_shift_rotas_EventSettingsId_TeamId",
                table: "rotas",
                newName: "IX_rotas_EventSettingsId_TeamId");

            migrationBuilder.RenameIndex(
                name: "IX_shift_rota_tags_ShiftTagId",
                table: "rota_shift_tags",
                newName: "IX_rota_shift_tags_ShiftTagId");

            migrationBuilder.RenameIndex(
                name: "IX_shift_general_availabilities_UserId_EventSettingsId",
                table: "general_availability",
                newName: "IX_general_availability_UserId_EventSettingsId");

            migrationBuilder.RenameIndex(
                name: "IX_shift_general_availabilities_EventSettingsId",
                table: "general_availability",
                newName: "IX_general_availability_EventSettingsId");

            migrationBuilder.RenameIndex(
                name: "IX_shift_event_settings_IsActive",
                table: "event_settings",
                newName: "IX_event_settings_IsActive");

            migrationBuilder.AddPrimaryKey(
                name: "PK_volunteer_tag_preferences",
                table: "volunteer_tag_preferences",
                column: "Id");

            migrationBuilder.AddPrimaryKey(
                name: "PK_volunteer_event_profiles",
                table: "volunteer_event_profiles",
                column: "Id");

            migrationBuilder.AddPrimaryKey(
                name: "PK_volunteer_build_statuses",
                table: "volunteer_build_statuses",
                column: "Id");

            migrationBuilder.AddPrimaryKey(
                name: "PK_rotas",
                table: "rotas",
                column: "Id");

            migrationBuilder.AddPrimaryKey(
                name: "PK_rota_shift_tags",
                table: "rota_shift_tags",
                columns: new[] { "RotaId", "ShiftTagId" });

            migrationBuilder.AddPrimaryKey(
                name: "PK_general_availability",
                table: "general_availability",
                column: "Id");

            migrationBuilder.AddPrimaryKey(
                name: "PK_event_settings",
                table: "event_settings",
                column: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_general_availability_event_settings_EventSettingsId",
                table: "general_availability",
                column: "EventSettingsId",
                principalTable: "event_settings",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_rota_shift_tags_rotas_RotaId",
                table: "rota_shift_tags",
                column: "RotaId",
                principalTable: "rotas",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_rota_shift_tags_shift_tags_ShiftTagId",
                table: "rota_shift_tags",
                column: "ShiftTagId",
                principalTable: "shift_tags",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_rotas_event_settings_EventSettingsId",
                table: "rotas",
                column: "EventSettingsId",
                principalTable: "event_settings",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_shifts_rotas_RotaId",
                table: "shifts",
                column: "RotaId",
                principalTable: "rotas",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_volunteer_build_statuses_event_settings_EventSettingsId",
                table: "volunteer_build_statuses",
                column: "EventSettingsId",
                principalTable: "event_settings",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_volunteer_tag_preferences_shift_tags_ShiftTagId",
                table: "volunteer_tag_preferences",
                column: "ShiftTagId",
                principalTable: "shift_tags",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
