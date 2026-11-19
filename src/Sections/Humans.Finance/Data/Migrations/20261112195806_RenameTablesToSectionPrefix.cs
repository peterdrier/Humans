using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Humans.Finance.Data.Migrations
{
    /// <inheritdoc />
    public partial class RenameTablesToSectionPrefix : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "PK_sepa_payout_transfers",
                table: "sepa_payout_transfers");

            migrationBuilder.DropPrimaryKey(
                name: "PK_sepa_payout_files",
                table: "sepa_payout_files");

            migrationBuilder.DropPrimaryKey(
                name: "PK_holded_managed_accounts",
                table: "holded_managed_accounts");

            migrationBuilder.DropPrimaryKey(
                name: "PK_holded_expense_docs",
                table: "holded_expense_docs");

            migrationBuilder.DropPrimaryKey(
                name: "PK_holded_doc_sync_state",
                table: "holded_doc_sync_state");

            migrationBuilder.DropPrimaryKey(
                name: "PK_holded_creditor_contacts",
                table: "holded_creditor_contacts");

            migrationBuilder.DropPrimaryKey(
                name: "PK_holded_category_map",
                table: "holded_category_map");

            migrationBuilder.RenameTable(
                name: "sepa_payout_transfers",
                newName: "finance_sepa_payout_transfers");

            migrationBuilder.RenameTable(
                name: "sepa_payout_files",
                newName: "finance_sepa_payout_files");

            migrationBuilder.RenameTable(
                name: "holded_managed_accounts",
                newName: "finance_holded_managed_accounts");

            migrationBuilder.RenameTable(
                name: "holded_expense_docs",
                newName: "finance_holded_expense_docs");

            migrationBuilder.RenameTable(
                name: "holded_doc_sync_state",
                newName: "finance_holded_doc_sync_states");

            migrationBuilder.RenameTable(
                name: "holded_creditor_contacts",
                newName: "finance_holded_creditor_contacts");

            migrationBuilder.RenameTable(
                name: "holded_category_map",
                newName: "finance_holded_category_maps");

            migrationBuilder.RenameIndex(
                name: "IX_sepa_payout_transfers_UserId",
                table: "finance_sepa_payout_transfers",
                newName: "IX_finance_sepa_payout_transfers_UserId");

            migrationBuilder.RenameIndex(
                name: "IX_sepa_payout_transfers_FileId",
                table: "finance_sepa_payout_transfers",
                newName: "IX_finance_sepa_payout_transfers_FileId");

            migrationBuilder.RenameIndex(
                name: "IX_sepa_payout_files_GeneratedAt",
                table: "finance_sepa_payout_files",
                newName: "IX_finance_sepa_payout_files_GeneratedAt");

            migrationBuilder.RenameIndex(
                name: "IX_holded_managed_accounts_HoldedAccountNumber",
                table: "finance_holded_managed_accounts",
                newName: "IX_finance_holded_managed_accounts_HoldedAccountNumber");

            migrationBuilder.RenameIndex(
                name: "IX_holded_managed_accounts_HoldedAccountId",
                table: "finance_holded_managed_accounts",
                newName: "IX_finance_holded_managed_accounts_HoldedAccountId");

            migrationBuilder.RenameIndex(
                name: "IX_holded_expense_docs_MatchStatus",
                table: "finance_holded_expense_docs",
                newName: "IX_finance_holded_expense_docs_MatchStatus");

            migrationBuilder.RenameIndex(
                name: "IX_holded_expense_docs_HoldedDocId",
                table: "finance_holded_expense_docs",
                newName: "IX_finance_holded_expense_docs_HoldedDocId");

            migrationBuilder.RenameIndex(
                name: "IX_holded_expense_docs_Date",
                table: "finance_holded_expense_docs",
                newName: "IX_finance_holded_expense_docs_Date");

            migrationBuilder.RenameIndex(
                name: "IX_holded_expense_docs_BudgetCategoryId",
                table: "finance_holded_expense_docs",
                newName: "IX_finance_holded_expense_docs_BudgetCategoryId");

            migrationBuilder.RenameIndex(
                name: "IX_holded_creditor_contacts_UserId",
                table: "finance_holded_creditor_contacts",
                newName: "IX_finance_holded_creditor_contacts_UserId");

            migrationBuilder.RenameIndex(
                name: "IX_holded_creditor_contacts_SupplierAccountNum",
                table: "finance_holded_creditor_contacts",
                newName: "IX_finance_holded_creditor_contacts_SupplierAccountNum");

            migrationBuilder.RenameIndex(
                name: "IX_holded_category_map_HoldedAccountNumber",
                table: "finance_holded_category_maps",
                newName: "IX_finance_holded_category_maps_HoldedAccountNumber");

            migrationBuilder.RenameIndex(
                name: "IX_holded_category_map_BudgetCategoryId",
                table: "finance_holded_category_maps",
                newName: "IX_finance_holded_category_maps_BudgetCategoryId");

            migrationBuilder.AddPrimaryKey(
                name: "PK_finance_sepa_payout_transfers",
                table: "finance_sepa_payout_transfers",
                column: "Id");

            migrationBuilder.AddPrimaryKey(
                name: "PK_finance_sepa_payout_files",
                table: "finance_sepa_payout_files",
                column: "Id");

            migrationBuilder.AddPrimaryKey(
                name: "PK_finance_holded_managed_accounts",
                table: "finance_holded_managed_accounts",
                column: "Id");

            migrationBuilder.AddPrimaryKey(
                name: "PK_finance_holded_expense_docs",
                table: "finance_holded_expense_docs",
                column: "Id");

            migrationBuilder.AddPrimaryKey(
                name: "PK_finance_holded_doc_sync_states",
                table: "finance_holded_doc_sync_states",
                column: "Id");

            migrationBuilder.AddPrimaryKey(
                name: "PK_finance_holded_creditor_contacts",
                table: "finance_holded_creditor_contacts",
                column: "Id");

            migrationBuilder.AddPrimaryKey(
                name: "PK_finance_holded_category_maps",
                table: "finance_holded_category_maps",
                column: "Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "PK_finance_sepa_payout_transfers",
                table: "finance_sepa_payout_transfers");

            migrationBuilder.DropPrimaryKey(
                name: "PK_finance_sepa_payout_files",
                table: "finance_sepa_payout_files");

            migrationBuilder.DropPrimaryKey(
                name: "PK_finance_holded_managed_accounts",
                table: "finance_holded_managed_accounts");

            migrationBuilder.DropPrimaryKey(
                name: "PK_finance_holded_expense_docs",
                table: "finance_holded_expense_docs");

            migrationBuilder.DropPrimaryKey(
                name: "PK_finance_holded_doc_sync_states",
                table: "finance_holded_doc_sync_states");

            migrationBuilder.DropPrimaryKey(
                name: "PK_finance_holded_creditor_contacts",
                table: "finance_holded_creditor_contacts");

            migrationBuilder.DropPrimaryKey(
                name: "PK_finance_holded_category_maps",
                table: "finance_holded_category_maps");

            migrationBuilder.RenameTable(
                name: "finance_sepa_payout_transfers",
                newName: "sepa_payout_transfers");

            migrationBuilder.RenameTable(
                name: "finance_sepa_payout_files",
                newName: "sepa_payout_files");

            migrationBuilder.RenameTable(
                name: "finance_holded_managed_accounts",
                newName: "holded_managed_accounts");

            migrationBuilder.RenameTable(
                name: "finance_holded_expense_docs",
                newName: "holded_expense_docs");

            migrationBuilder.RenameTable(
                name: "finance_holded_doc_sync_states",
                newName: "holded_doc_sync_state");

            migrationBuilder.RenameTable(
                name: "finance_holded_creditor_contacts",
                newName: "holded_creditor_contacts");

            migrationBuilder.RenameTable(
                name: "finance_holded_category_maps",
                newName: "holded_category_map");

            migrationBuilder.RenameIndex(
                name: "IX_finance_sepa_payout_transfers_UserId",
                table: "sepa_payout_transfers",
                newName: "IX_sepa_payout_transfers_UserId");

            migrationBuilder.RenameIndex(
                name: "IX_finance_sepa_payout_transfers_FileId",
                table: "sepa_payout_transfers",
                newName: "IX_sepa_payout_transfers_FileId");

            migrationBuilder.RenameIndex(
                name: "IX_finance_sepa_payout_files_GeneratedAt",
                table: "sepa_payout_files",
                newName: "IX_sepa_payout_files_GeneratedAt");

            migrationBuilder.RenameIndex(
                name: "IX_finance_holded_managed_accounts_HoldedAccountNumber",
                table: "holded_managed_accounts",
                newName: "IX_holded_managed_accounts_HoldedAccountNumber");

            migrationBuilder.RenameIndex(
                name: "IX_finance_holded_managed_accounts_HoldedAccountId",
                table: "holded_managed_accounts",
                newName: "IX_holded_managed_accounts_HoldedAccountId");

            migrationBuilder.RenameIndex(
                name: "IX_finance_holded_expense_docs_MatchStatus",
                table: "holded_expense_docs",
                newName: "IX_holded_expense_docs_MatchStatus");

            migrationBuilder.RenameIndex(
                name: "IX_finance_holded_expense_docs_HoldedDocId",
                table: "holded_expense_docs",
                newName: "IX_holded_expense_docs_HoldedDocId");

            migrationBuilder.RenameIndex(
                name: "IX_finance_holded_expense_docs_Date",
                table: "holded_expense_docs",
                newName: "IX_holded_expense_docs_Date");

            migrationBuilder.RenameIndex(
                name: "IX_finance_holded_expense_docs_BudgetCategoryId",
                table: "holded_expense_docs",
                newName: "IX_holded_expense_docs_BudgetCategoryId");

            migrationBuilder.RenameIndex(
                name: "IX_finance_holded_creditor_contacts_UserId",
                table: "holded_creditor_contacts",
                newName: "IX_holded_creditor_contacts_UserId");

            migrationBuilder.RenameIndex(
                name: "IX_finance_holded_creditor_contacts_SupplierAccountNum",
                table: "holded_creditor_contacts",
                newName: "IX_holded_creditor_contacts_SupplierAccountNum");

            migrationBuilder.RenameIndex(
                name: "IX_finance_holded_category_maps_HoldedAccountNumber",
                table: "holded_category_map",
                newName: "IX_holded_category_map_HoldedAccountNumber");

            migrationBuilder.RenameIndex(
                name: "IX_finance_holded_category_maps_BudgetCategoryId",
                table: "holded_category_map",
                newName: "IX_holded_category_map_BudgetCategoryId");

            migrationBuilder.AddPrimaryKey(
                name: "PK_sepa_payout_transfers",
                table: "sepa_payout_transfers",
                column: "Id");

            migrationBuilder.AddPrimaryKey(
                name: "PK_sepa_payout_files",
                table: "sepa_payout_files",
                column: "Id");

            migrationBuilder.AddPrimaryKey(
                name: "PK_holded_managed_accounts",
                table: "holded_managed_accounts",
                column: "Id");

            migrationBuilder.AddPrimaryKey(
                name: "PK_holded_expense_docs",
                table: "holded_expense_docs",
                column: "Id");

            migrationBuilder.AddPrimaryKey(
                name: "PK_holded_doc_sync_state",
                table: "holded_doc_sync_state",
                column: "Id");

            migrationBuilder.AddPrimaryKey(
                name: "PK_holded_creditor_contacts",
                table: "holded_creditor_contacts",
                column: "Id");

            migrationBuilder.AddPrimaryKey(
                name: "PK_holded_category_map",
                table: "holded_category_map",
                column: "Id");
        }
    }
}
