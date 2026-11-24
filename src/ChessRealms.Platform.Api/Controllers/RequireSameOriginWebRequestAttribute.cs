using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace ChessRealms.Platform.Api.Controllers;

[AttributeUsage(AttributeTargets.Class)]
internal sealed class RequireSameOriginWebRequestAttribute : Attribute, IResourceFilter
{
    public void OnResourceExecuting(ResourceExecutingContext context)
    {
        if (!Matches(context.HttpContext.Request))
        {
            context.Result = new StatusCodeResult(StatusCodes.Status403Forbidden);
        }
    }

    internal static bool Matches(HttpRequest request)
    {
        IHeaderDictionary headers = request.Headers;
        // A cross-origin browser needs CORS permission to set this non-safelisted header.
        // Keep these endpoints same-origin and reject cross-origin Fetch Metadata explicitly.
        return headers["X-ChessRealms-Web"] == "1"
            && (!headers.TryGetValue("Sec-Fetch-Site", out var site) || site == "same-origin");
    }

    public void OnResourceExecuted(ResourceExecutedContext context) { }
}
