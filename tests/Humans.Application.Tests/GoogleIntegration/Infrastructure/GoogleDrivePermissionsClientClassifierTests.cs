using AwesomeAssertions;
using Google.Apis.Requests;
using Humans.Infrastructure.Services.GoogleWorkspace;
using Xunit;

namespace Humans.Application.Tests.GoogleIntegration.Infrastructure;

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
    [HumansTheory]
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

    [HumansFact]
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

    [HumansFact]
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

    [HumansTheory]
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

    [HumansFact]
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

    [HumansFact]
    public void NullMessageAndNoInnerErrors_NotClassifiedAsDuplicate()
    {
        var error = new RequestError { Code = 400 };

        GoogleDrivePermissionsClient.IsDuplicatePermissionError(error).Should().BeFalse();
    }
}

/// <summary>
/// Focused unit tests for <see cref="GoogleDrivePermissionsClient.IsInheritedPermissionError"/>,
/// the classifier that decides whether an HTTP 403 from
/// <c>drive.permissions.delete</c> is the terminal "permission is inherited
/// from a parent folder" case (nobodies-collective/Humans#945) vs. any other
/// 403 that should surface to the caller for retry / investigation.
/// </summary>
public class GoogleDrivePermissionsClientInheritedErrorClassifierTests
{
    [HumansTheory]
    [InlineData("The authenticated user cannot delete the permission. If the permission is inherited, limited access must be leveraged.")]
    [InlineData("Permission is Inherited and cannot be removed directly")]
    public void MessageMentionsInherited_ClassifiedAsInheritedPermission(string message)
    {
        var error = new RequestError { Message = message, Code = 403 };

        GoogleDrivePermissionsClient.IsInheritedPermissionError(error)
            .Should().BeTrue(because: "'inherited' wording is Google's signal that the permission cannot be deleted at this level");
    }

    [HumansFact]
    public void InnerErrorReasonCannotDeletePermission_ClassifiedAsInheritedPermission()
    {
        var error = new RequestError
        {
            Code = 403,
            Message = "The user does not have sufficient permissions for this file.",
            Errors =
            [
                new SingleError { Reason = "cannotDeletePermission", Message = "Cannot delete inherited permission" }
            ]
        };

        GoogleDrivePermissionsClient.IsInheritedPermissionError(error).Should().BeTrue();
    }

    [HumansTheory]
    [InlineData("The user does not have sufficient permissions for this file.")]
    [InlineData("Rate limit exceeded")]
    [InlineData("Caller does not have permission")]
    public void GenericForbidden_NotClassifiedAsInheritedPermission(string message)
    {
        var error = new RequestError { Message = message, Code = 403 };

        GoogleDrivePermissionsClient.IsInheritedPermissionError(error)
            .Should().BeFalse(because: "non-inherited 403s must surface to the caller for retry / investigation");
    }

    [HumansFact]
    public void InnerErrorWithUnrelatedReason_NotClassifiedAsInheritedPermission()
    {
        var error = new RequestError
        {
            Code = 403,
            Message = "Forbidden.",
            Errors =
            [
                new SingleError { Reason = "insufficientFilePermissions", Message = "caller lacks permission" }
            ]
        };

        GoogleDrivePermissionsClient.IsInheritedPermissionError(error)
            .Should().BeFalse();
    }

    [HumansFact]
    public void NullMessageAndNoInnerErrors_NotClassifiedAsInheritedPermission()
    {
        var error = new RequestError { Code = 403 };

        GoogleDrivePermissionsClient.IsInheritedPermissionError(error).Should().BeFalse();
    }
}
