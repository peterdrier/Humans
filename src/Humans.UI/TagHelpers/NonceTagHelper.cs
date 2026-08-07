using Microsoft.AspNetCore.Razor.TagHelpers;

namespace Humans.Web.TagHelpers;

[HtmlTargetElement("script")]
public class NonceTagHelper(IHttpContextAccessor httpContextAccessor) : TagHelper
{
    public override void Process(TagHelperContext context, TagHelperOutput output)
    {
        var httpContext = httpContextAccessor.HttpContext;
        if (httpContext?.Items["CspNonce"] is string nonce)
        {
            output.Attributes.SetAttribute("nonce", nonce);
        }
    }
}
