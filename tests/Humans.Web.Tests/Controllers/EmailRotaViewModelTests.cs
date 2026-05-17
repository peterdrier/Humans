using System.ComponentModel.DataAnnotations;
using AwesomeAssertions;
using Humans.Web.Models.Shifts;

namespace Humans.Web.Tests.Controllers;

/// <summary>
/// Validation coverage for <see cref="EmailRotaViewModel"/> — the compose-form
/// model behind the coordinator "Email a rota" action
/// (nobodies-collective/Humans#732). The controller's redirect-on-success and
/// orchestrator wiring are covered by the orchestrator service tests; what
/// remains controller-shaped is purely model-binding/validation.
/// </summary>
public sealed class EmailRotaViewModelTests
{
    [HumansFact]
    public void Message_Empty_FailsValidation()
    {
        var vm = new EmailRotaViewModel { Message = string.Empty };
        var results = Validate(vm);

        results.Should().NotBeEmpty();
        results.Should().Contain(r => r.MemberNames.Contains(nameof(EmailRotaViewModel.Message), StringComparer.Ordinal));
    }

    [HumansFact]
    public void Message_TooLong_FailsValidation()
    {
        var vm = new EmailRotaViewModel { Message = new string('x', 4001) };
        var results = Validate(vm);

        results.Should().Contain(r => r.MemberNames.Contains(nameof(EmailRotaViewModel.Message), StringComparer.Ordinal));
    }

    [HumansFact]
    public void Message_ValidPayload_Passes()
    {
        var vm = new EmailRotaViewModel
        {
            RotaId = Guid.NewGuid(),
            RotaName = "Test Rota",
            TeamSlug = "test-team",
            Message = "Hello team — a quick note about Friday's shifts.",
        };

        Validate(vm).Should().BeEmpty();
    }

    private static IReadOnlyList<ValidationResult> Validate(EmailRotaViewModel vm)
    {
        var context = new ValidationContext(vm);
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(vm, context, results, validateAllProperties: true);
        return results;
    }
}
