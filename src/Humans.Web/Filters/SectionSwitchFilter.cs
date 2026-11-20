using Humans.Base.Interfaces;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Humans.Web.Filters;

/// <summary>
/// Answers 404 for a controller whose section is switched off for the active event, and publishes the switched-off
/// set on the request so the nav and dashboard view components drop that section's contributions.
/// </summary>
/// <remarks>
/// A resource filter so it runs before model binding and every action filter. Nobody is exempt, admins included: the
/// way back is the event form in Settings, which cannot be switched off.
/// </remarks>
public sealed class SectionSwitchFilter(ISectionSwitches switches) : IAsyncResourceFilter
{
    public async Task OnResourceExecutionAsync(ResourceExecutingContext context, ResourceExecutionDelegate next)
    {
        var switchedOff = await switches.GetSwitchedOffAsync(context.HttpContext.RequestAborted);
        context.HttpContext.SetSwitchedOffSections(switchedOff);

        if (switchedOff.Count > 0
            && context.ActionDescriptor is ControllerActionDescriptor descriptor
            && switchedOff.Contains(SectionSwitchboard.SectionNameOf(descriptor.ControllerTypeInfo)))
        {
            context.Result = new NotFoundResult();
            return;
        }

        await next();
    }
}
