using Humans.Web.Constants;
using Microsoft.AspNetCore.Mvc;

namespace Humans.Web.ViewComponents;

public class TempDataAlertsViewComponent : ViewComponent
{
    public IViewComponentResult Invoke()
    {
        var alerts = new List<TempDataAlert>();

        if (TempData[TempDataKeys.SuccessMessage] is string success)
            alerts.Add(new TempDataAlert("success", success));

        if (TempData[TempDataKeys.ErrorMessage] is string error)
            alerts.Add(new TempDataAlert("danger", error));

        if (TempData[TempDataKeys.InfoMessage] is string info)
            alerts.Add(new TempDataAlert("info", info));

        return View(alerts);
    }

    public record TempDataAlert(string CssClass, string Message);
}
