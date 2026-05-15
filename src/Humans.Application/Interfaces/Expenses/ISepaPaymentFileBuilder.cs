using Humans.Application.Services.Expenses.Dtos;
using NodaTime;

namespace Humans.Application.Interfaces.Expenses;

public interface ISepaPaymentFileBuilder : IApplicationService
{
    string BuildPain001(
        SepaConfig config,
        Instant generatedAt,
        IReadOnlyList<ExpenseReportDto> reports);
}
