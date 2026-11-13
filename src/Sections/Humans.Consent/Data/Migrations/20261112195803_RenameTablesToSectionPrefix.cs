using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Humans.Consent.Data.Migrations
{
    /// <inheritdoc />
    public partial class RenameTablesToSectionPrefix : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_consent_records_document_versions_DocumentVersionId",
                table: "consent_records");

            migrationBuilder.DropForeignKey(
                name: "FK_document_versions_legal_documents_LegalDocumentId",
                table: "document_versions");

            migrationBuilder.DropPrimaryKey(
                name: "PK_legal_documents",
                table: "legal_documents");

            migrationBuilder.DropPrimaryKey(
                name: "PK_document_versions",
                table: "document_versions");

            migrationBuilder.RenameTable(
                name: "legal_documents",
                newName: "consent_legal_documents");

            migrationBuilder.RenameTable(
                name: "document_versions",
                newName: "consent_document_versions");

            migrationBuilder.RenameIndex(
                name: "IX_legal_documents_TeamId_IsActive",
                table: "consent_legal_documents",
                newName: "IX_consent_legal_documents_TeamId_IsActive");

            migrationBuilder.RenameIndex(
                name: "IX_legal_documents_IsActive",
                table: "consent_legal_documents",
                newName: "IX_consent_legal_documents_IsActive");

            migrationBuilder.RenameIndex(
                name: "IX_document_versions_LegalDocumentId",
                table: "consent_document_versions",
                newName: "IX_consent_document_versions_LegalDocumentId");

            migrationBuilder.RenameIndex(
                name: "IX_document_versions_EffectiveFrom",
                table: "consent_document_versions",
                newName: "IX_consent_document_versions_EffectiveFrom");

            migrationBuilder.RenameIndex(
                name: "IX_document_versions_CommitSha",
                table: "consent_document_versions",
                newName: "IX_consent_document_versions_CommitSha");

            migrationBuilder.AddPrimaryKey(
                name: "PK_consent_legal_documents",
                table: "consent_legal_documents",
                column: "Id");

            migrationBuilder.AddPrimaryKey(
                name: "PK_consent_document_versions",
                table: "consent_document_versions",
                column: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_consent_document_versions_consent_legal_documents_LegalDocu~",
                table: "consent_document_versions",
                column: "LegalDocumentId",
                principalTable: "consent_legal_documents",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_consent_records_consent_document_versions_DocumentVersionId",
                table: "consent_records",
                column: "DocumentVersionId",
                principalTable: "consent_document_versions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_consent_document_versions_consent_legal_documents_LegalDocu~",
                table: "consent_document_versions");

            migrationBuilder.DropForeignKey(
                name: "FK_consent_records_consent_document_versions_DocumentVersionId",
                table: "consent_records");

            migrationBuilder.DropPrimaryKey(
                name: "PK_consent_legal_documents",
                table: "consent_legal_documents");

            migrationBuilder.DropPrimaryKey(
                name: "PK_consent_document_versions",
                table: "consent_document_versions");

            migrationBuilder.RenameTable(
                name: "consent_legal_documents",
                newName: "legal_documents");

            migrationBuilder.RenameTable(
                name: "consent_document_versions",
                newName: "document_versions");

            migrationBuilder.RenameIndex(
                name: "IX_consent_legal_documents_TeamId_IsActive",
                table: "legal_documents",
                newName: "IX_legal_documents_TeamId_IsActive");

            migrationBuilder.RenameIndex(
                name: "IX_consent_legal_documents_IsActive",
                table: "legal_documents",
                newName: "IX_legal_documents_IsActive");

            migrationBuilder.RenameIndex(
                name: "IX_consent_document_versions_LegalDocumentId",
                table: "document_versions",
                newName: "IX_document_versions_LegalDocumentId");

            migrationBuilder.RenameIndex(
                name: "IX_consent_document_versions_EffectiveFrom",
                table: "document_versions",
                newName: "IX_document_versions_EffectiveFrom");

            migrationBuilder.RenameIndex(
                name: "IX_consent_document_versions_CommitSha",
                table: "document_versions",
                newName: "IX_document_versions_CommitSha");

            migrationBuilder.AddPrimaryKey(
                name: "PK_legal_documents",
                table: "legal_documents",
                column: "Id");

            migrationBuilder.AddPrimaryKey(
                name: "PK_document_versions",
                table: "document_versions",
                column: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_consent_records_document_versions_DocumentVersionId",
                table: "consent_records",
                column: "DocumentVersionId",
                principalTable: "document_versions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_document_versions_legal_documents_LegalDocumentId",
                table: "document_versions",
                column: "LegalDocumentId",
                principalTable: "legal_documents",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
