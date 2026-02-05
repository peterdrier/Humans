using FluentAssertions;
using NodaTime;
using Profiles.Domain.Entities;
using Profiles.Domain.Enums;
using Xunit;

namespace Profiles.Domain.Tests.Entities;

public class ContactFieldTests
{
    [Theory]
    [InlineData(ContactFieldType.Email, "Email")]
    [InlineData(ContactFieldType.Phone, "Phone")]
    [InlineData(ContactFieldType.Signal, "Signal")]
    [InlineData(ContactFieldType.Telegram, "Telegram")]
    [InlineData(ContactFieldType.WhatsApp, "WhatsApp")]
    public void DisplayLabel_ForStandardType_ShouldReturnTypeName(ContactFieldType type, string expected)
    {
        var field = CreateContactField(type);

        field.DisplayLabel.Should().Be(expected);
    }

    [Fact]
    public void DisplayLabel_ForOtherType_WithCustomLabel_ShouldReturnCustomLabel()
    {
        var field = CreateContactField(ContactFieldType.Other);
        field.CustomLabel = "Discord";

        field.DisplayLabel.Should().Be("Discord");
    }

    [Fact]
    public void DisplayLabel_ForOtherType_WithNullCustomLabel_ShouldReturnOther()
    {
        var field = CreateContactField(ContactFieldType.Other);
        field.CustomLabel = null;

        field.DisplayLabel.Should().Be("Other");
    }

    [Fact]
    public void DisplayLabel_ForOtherType_WithEmptyCustomLabel_ShouldReturnEmptyString()
    {
        var field = CreateContactField(ContactFieldType.Other);
        field.CustomLabel = "";

        // Empty string is truthy in the null-coalescing context, so it returns ""
        field.DisplayLabel.Should().Be("");
    }

    private static ContactField CreateContactField(ContactFieldType type)
    {
        var now = SystemClock.Instance.GetCurrentInstant();
        return new ContactField
        {
            Id = Guid.NewGuid(),
            ProfileId = Guid.NewGuid(),
            FieldType = type,
            Value = "test@example.com",
            Visibility = ContactFieldVisibility.AllActiveProfiles,
            DisplayOrder = 0,
            CreatedAt = now,
            UpdatedAt = now
        };
    }
}
