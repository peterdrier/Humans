using AwesomeAssertions;
using Google.Apis.Requests;
using Humans.Infrastructure.Services.GoogleWorkspace;
using Xunit;

namespace Humans.Application.Tests.Infrastructure.GoogleWorkspace;

/// <summary>
/// Focused unit tests for <see cref="GoogleDrivePermissionsClient.IsDuplicatePermissionError"/>,
/// the classifier that decides whether an HTTP 400 from
/// <c>drive.permissions.create</c> should be treated as an idempotent
/// <see cref="Humans.Application.Interfaces.GoogleIntegration.DrivePermissionCreateOutcome.AlreadyExists"/>
/// (the user already has a permission) or a real failure that should
/// surface to the caller for retry / investigation.
///
/// <para>
/// Per Codex's §15 Part 2a review (PR #302): treating every 400 as
/// "already exists" silently swallows malformed-payload / bad-role /
/// rate-limit failures. This classifier is the guard that prevents the
/// regression.
/// </para>
/// </summary>
public class GoogleDrivePermissionsClientClassifierTests
{
    [Theory]
    [InlineData("A permission with the specified email address already exists")]
    [InlineData("A permission already exists for this user")]
    [InlineData("Already exists")]
    [InlineData("ALREADY EXIST")]
    public void MessageMentionsAlreadyExists_ClassifiedAsDuplicate(string message)
    {
        var error = new RequestError { Message = message, Code = 400 };

        GoogleDrivePermissionsClient.IsDuplicatePermissionError(error)
            .Should().BeTrue(because: "'already exist' wording is Google's idempotency signal");
    }

    [Fact]
    public void InnerErrorReasonDuplicate_ClassifiedAsDuplicate()
    {
        var error = new RequestError
        {
            Code = 400,
            Message = "Bad request.",
            Errors =
            [
                new SingleError { Reason = "duplicate", Message = "duplicate entry" }
            ]
        };

        GoogleDrivePermissionsClient.IsDuplicatePermissionError(error).Should().BeTrue();
    }

    [Fact]
    public void InnerErrorReasonAlreadyExists_ClassifiedAsDuplicate()
    {
        var error = new RequestError
        {
            Code = 400,
            Message = "Bad request.",
            Errors =
            [
                new SingleError { Reason = "alreadyExists" }
            ]
        };

        GoogleDrivePermissionsClient.IsDuplicatePermissionError(error).Should().BeTrue();
    }

    [Theory]
    [InlineData("Bad request.")]
    [InlineData("The role 'superuser' is not supported")]
    [InlineData("Sharing rate limit exceeded")]
    [InlineData("Cannot share this item")]
    [InlineData("Invalid permission body")]
    public void GenericBadRequest_NotClassifiedAsDuplicate(string message)
    {
        var error = new RequestError { Message = message, Code = 400 };

        GoogleDrivePermissionsClient.IsDuplicatePermissionError(error)
            .Should().BeFalse(because: "non-duplicate 400s must surface to the caller for retry / investigation");
    }

    [Fact]
    public void InnerErrorWithUnrelatedReason_NotClassifiedAsDuplicate()
    {
        var error = new RequestError
        {
            Code = 400,
            Message = "Bad request.",
            Errors =
            [
                new SingleError { Reason = "invalidSharingRequest", Message = "shared drive requires manager role" }
            ]
        };

        GoogleDrivePermissionsClient.IsDuplicatePermissionError(error)
            .Should().BeFalse();
    }

    [Fact]
    public void NullMessageAndNoInnerErrors_NotClassifiedAsDuplicate()
    {
        var error = new RequestError { Code = 400 };

        GoogleDrivePermissionsClient.IsDuplicatePermissionError(error).Should().BeFalse();
    }
}
