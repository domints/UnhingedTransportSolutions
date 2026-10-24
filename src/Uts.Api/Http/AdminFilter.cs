namespace Uts.Api.Http;

/// <summary>
/// Gate for /api/admin/*: requires an authenticated user whose OIDC token
/// carries the configured admin group (e.g. <c>uts-admins</c>).
/// </summary>
public sealed class AdminFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        var cfg = http.RequestServices.GetRequiredService<IConfiguration>();

        if (http.User.Identity?.IsAuthenticated != true)
            return Results.Unauthorized();

        var groupsClaim = cfg["OIDC:GroupsClaim"] ?? "groups";
        var adminGroup = cfg["Auth:AdminGroup"] ?? "uts-admins";

        if (!http.User.HasClaim(groupsClaim, adminGroup))
            return Results.StatusCode(StatusCodes.Status403Forbidden);

        return await next(context);
    }
}
