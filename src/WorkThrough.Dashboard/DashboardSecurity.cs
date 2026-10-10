using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace WorkThrough;

public static class DashboardPermissions
{
    public static IReadOnlyList<string> All => OperationPermissions.All;
    public const string ClaimType = OperationPermissions.ClaimType;
    public const string PolicyPrefix = OperationPermissions.PolicyPrefix;
}

/// <summary>Host policies may authorize a configured store or an individual route resource. Correlation is not an access boundary.</summary>
public sealed record DashboardResource(string Path, IReadOnlyDictionary<string, object?> RouteValues);

public static partial class DashboardEndpoints
{
    internal const string CsrfHeader = "X-CSRF-TOKEN";
    private const string SubjectClaim = "sub";
    private const string IssuerClaim = "iss";
    private const string DevelopmentUser = "Development user";
    private static readonly OperationActor DevelopmentActor = new("workthrough-development", "anonymous", DevelopmentUser);

    private static Task<bool> AllowedAsync(HttpContext context, JobDashboardOptions options, string permission)
    {
        if (options.AllowAnonymous)
        {
            return Task.FromResult(true);
        }

        return AuthorizeAsync();

        async Task<bool> AuthorizeAsync()
        {
            var resource = new DashboardResource(context.Request.Path, new Dictionary<string, object?>(context.Request.RouteValues));
            var authorization = context.RequestServices.GetRequiredService<IAuthorizationService>();

            return (await authorization.AuthorizeAsync(context.User, resource, OperationPermissions.Policy(permission))).Succeeded;
        }
    }

    private static RouteHandlerBuilder WithPermission(this RouteHandlerBuilder route, JobDashboardOptions options, string permission) =>
        route.AddEndpointFilter(async (invocation, next) =>
            await AllowedAsync(invocation.HttpContext, options, permission) ? await next(invocation) : Results.Forbid());

    private static OperationActor? FindActor(HttpContext context, JobDashboardOptions options)
    {
        if (options.AllowAnonymous && context.User.Identity?.IsAuthenticated != true)
        {
            return DevelopmentActor;
        }

        var subject = context.User.FindFirst(SubjectClaim) ?? context.User.FindFirst(ClaimTypes.NameIdentifier);

        return subject is null ? null : new(context.User.FindFirst(IssuerClaim)?.Value ?? subject.Issuer, subject.Value, context.User.Identity?.Name);
    }

    /// <summary>Only called behind <see cref="Mutation"/>, which has already verified that a trusted actor exists.</summary>
    private static AdministrativeCommand Command(HttpContext context, JobDashboardOptions options, DashboardCommand command) =>
        new(command.CommandId, FindActor(context, options)!, command.Reason);

    private static RouteHandlerBuilder Mutation(this RouteHandlerBuilder route, JobDashboardOptions options, string permission) =>
        route.WithPermission(options, permission).AddEndpointFilter(async (invocation, next) =>
        {
            var context = invocation.HttpContext;
            if (!await ValidTokenAsync(context, context.RequestServices.GetRequiredService<IAntiforgery>()))
            {
                return Results.BadRequest(new { error = "Invalid anti-forgery token." });
            }

            if (FindActor(context, options) is null)
            {
                return Results.Json(new { error = "Dashboard operators need a trusted sub or NameIdentifier claim." }, statusCode: StatusCodes.Status403Forbidden);
            }

            return await next(invocation);
        });

    private static async Task<bool> ValidTokenAsync(HttpContext context, IAntiforgery antiforgery)
    {
        try
        {
            await antiforgery.ValidateRequestAsync(context);

            return true;
        }
        catch (AntiforgeryValidationException)
        {
            return false;
        }
    }

    private static IResult ToHttp(Result result) => result.IsSuccess ? Results.Json(true) : Failure(result.Error);

    private static IResult ToHttp<T>(Result<T> result) => result.IsSuccess ? Results.Json(result.Value, JsonOptions) : Failure(result.Error);

    private static IResult Failure(Error error) => error.Type switch
    {
        ErrorType.Validation => Results.BadRequest(new { error = error.Message }),
        ErrorType.NotFound => Results.NotFound(new { error = error.Message }),
        _ => Results.Conflict(new { error = error.Message })
    };

    private static IResult Unavailable(string message) => Results.Problem(message, statusCode: StatusCodes.Status503ServiceUnavailable);
}
