using System.Security.Claims;
using AeroSynapse.Infrastructure.Identity;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using AeroSynapse.Infrastructure.Governance;

namespace AeroSynapse.API.Identity;

public sealed record LoginRequest(string Username, string Password);
public sealed record SessionView(string Username, string[] Roles, DateTime ExpiresAtUtc, string TenantId = "demo");

internal static class IdentityEndpoints
{
    public static void MapIdentityEndpoints(this WebApplication app)
    {
        var auth = app.MapGroup("/auth").WithTags("Workspace sessions");
        auth.MapGet("/csrf", (HttpContext context, IAntiforgery csrf) =>
            Results.Ok(new { requestToken = csrf.GetAndStoreTokens(context).RequestToken })).AllowAnonymous();
        auth.MapPost("/login", async (LoginRequest request, HttpContext context, UserManager<WorkspaceUser> users,
            SignInManager<WorkspaceUser> signIn, IdentityStore store, TimeProvider clock, IOptions<SessionOptions> options) =>
        {
            if (string.IsNullOrWhiteSpace(request.Username) || request.Username.Length > 256 || string.IsNullOrEmpty(request.Password) || request.Password.Length > 1024)
                return WorkspaceIdentity.Problem(400, "login_invalid", "Enter a username and password.");
            var user = await users.FindByNameAsync(request.Username);
            if (user is null || user.Disabled || !context.RequestServices.GetRequiredService<TenantCatalog>().Contains(user.TenantId))
            {
                // Perform password hashing even for unknown/disabled accounts.
                _ = users.PasswordHasher.HashPassword(new WorkspaceUser(), request.Password);
                return WorkspaceIdentity.Problem(401, "login_failed", "Credentials are invalid or access is unavailable.");
            }
            var result = await signIn.CheckPasswordSignInAsync(user, request.Password, lockoutOnFailure: true);
            if (!result.Succeeded) return WorkspaceIdentity.Problem(401, "login_failed", "Credentials are invalid or access is unavailable.");
            var expires = clock.GetUtcNow().AddMinutes(options.Value.DurationMinutes);
            var session = new WorkspaceSession { Id = Guid.NewGuid(), UserId = user.Id,
                SecurityStamp = (await users.GetSecurityStampAsync(user)), ExpiresAtUtc = expires.UtcDateTime };
            store.Sessions.Add(session);
            await store.SaveChangesAsync(context.RequestAborted);
            var principal = await signIn.CreateUserPrincipalAsync(user);
            ((ClaimsIdentity)principal.Identity!).AddClaim(new(WorkspaceIdentity.SessionClaim, session.Id.ToString()));
            ((ClaimsIdentity)principal.Identity!).AddClaim(new(TenantContext.Claim, user.TenantId));
            await context.SignInAsync(IdentityConstants.ApplicationScheme, principal,
                new AuthenticationProperties { IsPersistent = false, AllowRefresh = false, IssuedUtc = clock.GetUtcNow(), ExpiresUtc = expires });
            return Results.Ok(new SessionView(user.UserName!, (await users.GetRolesAsync(user)).ToArray(), expires.UtcDateTime, user.TenantId));
        }).AllowAnonymous().RequireRateLimiting("login");
        auth.MapGet("/session", async (HttpContext context, IdentityStore store) =>
        {
            var id = Guid.Parse(context.User.FindFirstValue(WorkspaceIdentity.SessionClaim)!);
            var session = await store.Sessions.AsNoTracking().SingleOrDefaultAsync(item => item.Id == id, context.RequestAborted);
            if (session is null) return WorkspaceIdentity.Problem(401, "session_revoked", "The session was revoked.");
            return Results.Ok(new SessionView(context.User.Identity!.Name!, context.User.FindAll(ClaimTypes.Role).Select(role => role.Value).ToArray(), session.ExpiresAtUtc, TenantContext.Id));
        }).RequireAuthorization(policy => policy.RequireAuthenticatedUser());
        auth.MapPost("/logout", async (HttpContext context, IdentityStore store) =>
        {
            var id = Guid.Parse(context.User.FindFirstValue(WorkspaceIdentity.SessionClaim)!);
            await store.Sessions.Where(item => item.Id == id).ExecuteDeleteAsync(context.RequestAborted);
            await context.SignOutAsync(IdentityConstants.ApplicationScheme);
            return Results.NoContent();
        }).RequireAuthorization(policy => policy.RequireAuthenticatedUser());

    }
}
