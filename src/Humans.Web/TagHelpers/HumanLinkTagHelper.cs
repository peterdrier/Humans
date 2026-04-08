using System.Text.Encodings.Web;
using Humans.Application.Interfaces;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Razor.TagHelpers;

namespace Humans.Web.TagHelpers;

/// <summary>
/// Renders a link to a human's profile with configurable display modes.
/// Replaces all raw asp-controller="Profile" anchor tags across the app.
/// </summary>
[HtmlTargetElement("human-link", TagStructure = TagStructure.NormalOrSelfClosing)]
public class HumanLinkTagHelper : TagHelper
{
    private readonly IUrlHelperFactory _urlHelperFactory;
    private readonly IProfileService _profileService;

    public HumanLinkTagHelper(IUrlHelperFactory urlHelperFactory, IProfileService profileService)
    {
        _urlHelperFactory = urlHelperFactory;
        _profileService = profileService;
    }

    [HtmlAttributeNotBound]
    [ViewContext]
    public ViewContext ViewContext { get; set; } = null!;

    /// <summary>User ID (required).</summary>
    [HtmlAttributeName("user-id")]
    public Guid UserId { get; set; }

    /// <summary>Display name (required).</summary>
    [HtmlAttributeName("display-name")]
    public string DisplayName { get; set; } = "";

    /// <summary>Profile picture URL (optional). If null, shows initial fallback.</summary>
    [HtmlAttributeName("profile-picture-url")]
    public string? ProfilePictureUrl { get; set; }

    /// <summary>
    /// Display mode: text (default), avatar, avatar-name, card.
    /// </summary>
    [HtmlAttributeName("mode")]
    public HumanLinkMode Mode { get; set; } = HumanLinkMode.Text;

    /// <summary>Avatar size in pixels. Only used for avatar/avatar-name/card modes. Default: 40.</summary>
    [HtmlAttributeName("size")]
    public int Size { get; set; } = 40;

    /// <summary>If true, links to the admin HumanDetail page instead of the public View page.</summary>
    [HtmlAttributeName("admin")]
    public bool Admin { get; set; }

    /// <summary>Enable hover popover with profile summary. Default: true.</summary>
    [HtmlAttributeName("show-popover")]
    public bool ShowPopover { get; set; } = true;

    /// <summary>Additional CSS class(es) for the avatar element.</summary>
    [HtmlAttributeName("avatar-css-class")]
    public string? AvatarCssClass { get; set; }

    /// <summary>Background color class for initial fallback avatar. Default: bg-secondary.</summary>
    [HtmlAttributeName("avatar-bg-color")]
    public string AvatarBgColor { get; set; } = "bg-secondary";

    public override async Task ProcessAsync(TagHelperContext context, TagHelperOutput output)
    {
        // Respect suppression from earlier tag helpers (e.g., AuthorizeViewTagHelper)
        if (output.TagName is null)
            return;

        // Resolve display name and profile picture from cache when not explicitly provided
        if (string.IsNullOrEmpty(DisplayName) && UserId != Guid.Empty)
        {
            var cached = await _profileService.GetCachedProfileAsync(UserId);
            if (cached is not null)
            {
                DisplayName = cached.DisplayName;
                ProfilePictureUrl ??= cached.ProfilePictureUrl;
            }
            else
            {
                DisplayName = "Unknown";
            }
        }

        var urlHelper = _urlHelperFactory.GetUrlHelper(ViewContext);
        var href = Admin
            ? urlHelper.Action("AdminDetail", "Profile", new { id = UserId })
            : urlHelper.Action("ViewProfile", "Profile", new { id = UserId });

        output.TagName = "a";
        output.TagMode = TagMode.StartTagAndEndTag;
        output.Attributes.SetAttribute("href", href);

        if (ShowPopover)
        {
            output.Attributes.Add("data-human-popover", "true");
            output.Attributes.Add("data-user-id", UserId.ToString());
        }

        var childContent = await output.GetChildContentAsync();

        switch (Mode)
        {
            case HumanLinkMode.Text:
                MergeClass(output, "text-decoration-none");
                if (childContent.IsEmptyOrWhiteSpace)
                {
                    output.Content.Append(DisplayName);
                }
                else
                {
                    output.Content.SetHtmlContent(childContent.GetContent());
                }
                break;

            case HumanLinkMode.Avatar:
                if (!output.Attributes.ContainsName("title"))
                {
                    output.Attributes.SetAttribute("title", DisplayName);
                }
                output.Content.SetHtmlContent(RenderAvatar());
                break;

            case HumanLinkMode.AvatarName:
                MergeClass(output, "d-flex align-items-center text-decoration-none text-reset");
                var avatarHtml = RenderAvatar(extraCssClass: "me-2");
                var nameHtml = HtmlEncoder.Default.Encode(DisplayName);
                var subContent = childContent.IsEmptyOrWhiteSpace
                    ? ""
                    : childContent.GetContent();
                output.Content.SetHtmlContent(
                    $"{avatarHtml}<div><div class=\"fw-semibold\">{nameHtml}</div>{subContent}</div>");
                break;

            case HumanLinkMode.Card:
                MergeClass(output, "text-center text-decoration-none d-block");
                var cardAvatar = RenderAvatar(extraCssClass: "mb-2 mx-auto");
                var cardName = HtmlEncoder.Default.Encode(DisplayName);
                var cardChild = childContent.IsEmptyOrWhiteSpace
                    ? ""
                    : childContent.GetContent();
                output.Content.SetHtmlContent(
                    $"{cardAvatar}<div class=\"small\">{cardName}</div>{cardChild}");
                break;
        }
    }

    private string RenderAvatar(string? extraCssClass = null)
    {
        var cssClasses = new List<string>();
        if (!string.IsNullOrEmpty(AvatarCssClass)) cssClasses.Add(AvatarCssClass);
        if (!string.IsNullOrEmpty(extraCssClass)) cssClasses.Add(extraCssClass);
        var cssClassStr = string.Join(" ", cssClasses);

        if (!string.IsNullOrEmpty(ProfilePictureUrl))
        {
            var altEncoded = HtmlEncoder.Default.Encode(DisplayName);
            return $"<img src=\"{HtmlEncoder.Default.Encode(ProfilePictureUrl)}\" alt=\"{altEncoded}\" " +
                   $"class=\"rounded-circle {cssClassStr}\" " +
                   $"style=\"width: {Size}px; height: {Size}px; object-fit: cover;\" />";
        }

        var initial = string.IsNullOrEmpty(DisplayName) ? "?" : DisplayName[..1];
        var fontRem = Math.Round(Size / 100.0 * 2.0, 1);
        if (fontRem < 0.7) fontRem = 0.7;

        return $"<div class=\"{AvatarBgColor} rounded-circle d-flex align-items-center justify-content-center text-white {cssClassStr}\" " +
               $"style=\"width: {Size}px; height: {Size}px; font-size: {fontRem}rem;\">" +
               $"{HtmlEncoder.Default.Encode(initial)}</div>";
    }

    private static void MergeClass(TagHelperOutput output, string classes)
    {
        if (output.Attributes.TryGetAttribute("class", out var existing))
        {
            output.Attributes.SetAttribute("class", $"{classes} {existing.Value}");
        }
        else
        {
            output.Attributes.SetAttribute("class", classes);
        }
    }
}

public enum HumanLinkMode
{
    Text,
    Avatar,
    AvatarName,
    Card
}
