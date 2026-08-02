using System.ComponentModel.DataAnnotations;
using AwesomeAssertions;
using Humans.Web.Models;

namespace Humans.Web.Tests.Controllers;

/// <summary>
/// Validation coverage for <see cref="SendMessageViewModel"/>. Browsers submit
/// textarea line breaks as \r\n, which — uncorrected — inflates the bound string
/// past what the user counted and pushed a 1985-character message with line
/// breaks over the 2000-char limit (nobodies-collective/Humans#953). The
/// property normalises \r\n to \n on bind so the length check agrees with the
/// user's count.
/// </summary>
public sealed class SendMessageViewModelTests
{
    [HumansFact]
    public void Message_CrLfNormalized_OnSet()
    {
        var vm = new SendMessageViewModel { Message = "line one\r\nline two" };

        vm.Message.Should().Be("line one\nline two");
    }

    [HumansFact]
    public void Message_1985CharsWithCrLfLineBreaks_PassesValidation()
    {
        // Build the message as the user (and a client-side char counter reading a
        // textarea's .value) would see it: exactly 1985 chars, one line break every
        // 100 chars (~19 line breaks total).
        var chars = new char[1985];
        Array.Fill(chars, 'x');
        for (var i = 99; i < chars.Length; i += 100)
            chars[i] = '\n';
        var userPerceived = new string(chars);
        var lineBreakCount = userPerceived.Count(c => c == '\n');

        // The browser submits textarea line breaks as \r\n, so this is what actually
        // hits the server-bound property — 1985 + lineBreakCount chars
        // pre-normalisation, which pushes it past the 2000-char StringLength check
        // without the fix.
        var rawSubmitted = userPerceived.Replace("\n", "\r\n", StringComparison.Ordinal);
        rawSubmitted.Length.Should().Be(1985 + lineBreakCount);
        rawSubmitted.Length.Should().BeGreaterThan(2000);

        var vm = new SendMessageViewModel { Message = rawSubmitted };

        vm.Message.Length.Should().Be(1985);
        Validate(vm).Should().BeEmpty();
    }

    [HumansFact]
    public void Message_TooLong_AfterNormalization_FailsValidation()
    {
        var vm = new SendMessageViewModel { Message = new string('x', 2001) };

        Validate(vm).Should().Contain(r => r.MemberNames.Contains(nameof(SendMessageViewModel.Message), StringComparer.Ordinal));
    }

    [HumansFact]
    public void Message_Empty_FailsValidation()
    {
        var vm = new SendMessageViewModel { Message = string.Empty };

        Validate(vm).Should().Contain(r => r.MemberNames.Contains(nameof(SendMessageViewModel.Message), StringComparer.Ordinal));
    }

    private static IReadOnlyList<ValidationResult> Validate(SendMessageViewModel vm)
    {
        var context = new ValidationContext(vm);
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(vm, context, results, validateAllProperties: true);
        return results;
    }
}
