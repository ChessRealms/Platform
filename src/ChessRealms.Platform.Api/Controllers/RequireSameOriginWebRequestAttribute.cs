using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace ChessRealms.Platform.Api.Controllers;

[AttributeUsage(AttributeTargets.Class)]
internal sealed class RequireSameOriginWebRequestAttribute : Attribute, IResourceFilter
{
    public void OnResourceExecuting(ResourceExecutingContext context)
    {
        IHeaderDictionary headers = context.HttpContext.Request.Headers;
        // A cross-origin browser needs CORS permission to set this non-safelisted header.
        // Keep these endpoints same-origin and reject cross-origin Fetch Metadata explicitly.
        if (headers["X-ChessRealms-Web"] != "1"
            || (headers.TryGetValue("Sec-Fetch-Site", out var site) && site != "same-origin"))
        {
            context.Result = new StatusCodeResult(StatusCodes.Status403Forbidden);
        }
    }

    public void OnResourceExecuted(ResourceExecutedContext context) { }
}
