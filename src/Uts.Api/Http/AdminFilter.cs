namespace Uts.Api.Http;

/// <summary>
/// Gate for /api/admin/* (groups call <c>AddEndpointFilter&lt;AdminFilter&gt;()</c>).
/// Two acceptance paths:
/// <list type="bullet">
///   <item>a cookie user whose OIDC token carries the configured admin group
///   (e.g. <c>uts-admins</c>) — full access, permissions for humans are the
///   group membership, not per-endpoint attributes;</item>
///   <item>an API-key principal (set by <see cref="ApiKeyMiddleware"/>) that
///   holds the permission declared on the endpoint via
///   <see cref="AdminPermAttribute"/>.</item>
/// </list>
/// Key principals are denied on endpoints without a permission attribute and
/// on anything marked <see cref="AdminKeysOnlyAttribute"/> — identity and key
/// management stay session-only; a key can never manage keys.
/// </summary>
public sealed class AdminFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;

        if (http.User.Identity?.IsAuthenticated != true)
            return Results.Unauthorized();

        if (http.Items.ContainsKey(ApiKeyMiddleware.ItemKeyAuth))
        {
            var endpoint = http.GetEndpoint();
            // No permission declared (identity/state endpoints) or key-management
            // itself → keys never get in, however wide their permission set.
            if (endpoint?.Metadata.GetMetadata<AdminKeysOnlyAttribute>() is not null)
                return Results.StatusCode(StatusCodes.Status403Forbidden);

            var perm = endpoint?.Metadata.GetMetadata<AdminPermAttribute>()?.Perm;
            if (perm is null)
                return Results.StatusCode(StatusCodes.Status403Forbidden);

            return http.User.HasClaim("perms", perm)
                ? await next(context)
                : Results.StatusCode(StatusCodes.Status403Forbidden);
        }

        var cfg = http.RequestServices.GetRequiredService<IConfiguration>();
        var groupsClaim = cfg["OIDC:GroupsClaim"] ?? "groups";
        var adminGroup = cfg["Auth:AdminGroup"] ?? "uts-admins";

        if (!http.User.HasClaim(groupsClaim, adminGroup))
            return Results.StatusCode(StatusCodes.Status403Forbidden);

        return await next(context);
    }
}
