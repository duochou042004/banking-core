using System.Security.Claims;
using BankingCore.Ledger.Model;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;

namespace BankingCore.Api.Authentication;

/// <summary>
/// Token validation settings for the administration and test API.
/// </summary>
/// <remarks>
/// <para>
/// The project does not implement identity. Tokens are validated with the standard ASP.NET Core JWT
/// bearer handler against an external authority (docs/architecture/technology-strategy.md, "Avoid by
/// default"; evaluation AG-017).
/// </para>
/// <para>
/// A symmetric-key profile exists so a developer or CI job can exercise the API without standing up
/// an identity provider. It is refused outright in the Production environment, and it is not a
/// substitute for the OIDC authority a real deployment must configure.
/// </para>
/// </remarks>
public sealed class LedgerAuthenticationOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Ledger:Authentication";

    /// <summary>OIDC authority that issues and signs access tokens. Required outside development.</summary>
    public string? Authority { get; set; }

    /// <summary>Expected audience of an access token.</summary>
    public string Audience { get; set; } = "banking-core-ledger";

    /// <summary>Expected issuer when using the symmetric development profile.</summary>
    public string Issuer { get; set; } = "banking-core-local";

    /// <summary>
    /// Base64 symmetric signing key for the development profile. Ignored when
    /// <see cref="Authority"/> is set, and rejected in the Production environment.
    /// </summary>
    public string? DevelopmentSymmetricKey { get; set; }
}

/// <summary>Claim types the API reads from a validated access token.</summary>
public static class LedgerClaims
{
    /// <summary>Tenant the principal acts for. Never taken from a route or body.</summary>
    public const string TenantId = "tenant_id";

    /// <summary>Legal entity the principal books against.</summary>
    public const string LegalEntityId = "legal_entity_id";

    /// <summary>Space-delimited granted scopes.</summary>
    public const string Scope = "scope";

    /// <summary>Whether the principal is a person or a workload.</summary>
    public const string ActorType = "actor_type";
}

/// <summary>Authorization policy names, one per separable duty.</summary>
public static class LedgerPolicies
{
    /// <summary>Commit financial facts.</summary>
    public const string Post = "ledger.post";

    /// <summary>Reverse a posted journal.</summary>
    public const string Reverse = "ledger.reverse";

    /// <summary>Read accounts, journals, and statements.</summary>
    public const string Read = "ledger.read";

    /// <summary>Administer assets, ledgers, accounts, and periods.</summary>
    public const string Administer = "ledger.admin";

    /// <summary>Run projections, the outbox relay, and reconciliation.</summary>
    public const string Operate = "ledger.operate";

    /// <summary>Every policy in declaration order.</summary>
    public static IReadOnlyList<string> All { get; } = [Post, Reverse, Read, Administer, Operate];
}

/// <summary>The authenticated caller, resolved once per request.</summary>
/// <param name="Scope">Tenant and legal entity taken from validated claims.</param>
/// <param name="Authority">Actor identity and the authorization decision reference.</param>
/// <param name="PrincipalId">The subject identifier used to scope idempotency keys.</param>
public sealed record LedgerCaller(LedgerScope Scope, CommandAuthority Authority, string PrincipalId);

/// <summary>Resolves the caller from validated claims.</summary>
public static class LedgerCallerResolver
{
    /// <summary>
    /// Builds the caller from the principal's claims.
    /// </summary>
    /// <remarks>
    /// The tenant and legal entity come only from the token. A tenant identifier appearing in a
    /// route, query string, or body is never trusted (evaluation AG-011).
    /// </remarks>
    public static LedgerCaller? Resolve(ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);

        var subject = principal.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? principal.FindFirstValue("sub");
        if (string.IsNullOrWhiteSpace(subject))
        {
            return null;
        }

        if (!Guid.TryParse(principal.FindFirstValue(LedgerClaims.TenantId), out var tenantId)
            || !Guid.TryParse(principal.FindFirstValue(LedgerClaims.LegalEntityId), out var legalEntityId))
        {
            return null;
        }

        var actorType = principal.FindFirstValue(LedgerClaims.ActorType) == "user"
            ? BankingCore.Ledger.Model.ActorType.User
            : BankingCore.Ledger.Model.ActorType.Workload;

        // The authorization decision identifier is generated per request so the audit record can be
        // correlated with the decision that permitted it. A production deployment replaces this with
        // the identifier issued by its policy decision point.
        var authority = new CommandAuthority(subject, actorType, Guid.NewGuid());
        return new LedgerCaller(new LedgerScope(tenantId, legalEntityId), authority, subject);
    }
}

/// <summary>Wires JWT bearer authentication and the scope-based authorization policies.</summary>
public static class LedgerAuthenticationExtensions
{
    /// <summary>Adds authentication and authorization for the ledger API.</summary>
    public static IServiceCollection AddLedgerAuthentication(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(environment);

        var options = new LedgerAuthenticationOptions();
        configuration.GetSection(LedgerAuthenticationOptions.SectionName).Bind(options);

        if (environment.IsProduction() && !string.IsNullOrWhiteSpace(options.DevelopmentSymmetricKey))
        {
            throw new InvalidOperationException(
                $"{LedgerAuthenticationOptions.SectionName}:DevelopmentSymmetricKey must not be set in Production. "
                + "Configure an OIDC Authority instead.");
        }

        if (string.IsNullOrWhiteSpace(options.Authority) && string.IsNullOrWhiteSpace(options.DevelopmentSymmetricKey))
        {
            throw new InvalidOperationException(
                $"Configure either {LedgerAuthenticationOptions.SectionName}:Authority or, outside Production, "
                + $"{LedgerAuthenticationOptions.SectionName}:DevelopmentSymmetricKey.");
        }

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(jwt =>
            {
                jwt.MapInboundClaims = false;
                jwt.RequireHttpsMetadata = !environment.IsDevelopment();
                jwt.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidateAudience = true,
                    ValidateLifetime = true,
                    ValidateIssuerSigningKey = true,
                    ValidAudience = options.Audience,
                    ClockSkew = TimeSpan.FromSeconds(30),
                    NameClaimType = "sub",
                };

                if (!string.IsNullOrWhiteSpace(options.Authority))
                {
                    jwt.Authority = options.Authority;
                    jwt.TokenValidationParameters.ValidIssuer = options.Authority;
                }
                else
                {
                    jwt.TokenValidationParameters.ValidIssuer = options.Issuer;
                    jwt.TokenValidationParameters.IssuerSigningKey = new SymmetricSecurityKey(
                        Convert.FromBase64String(options.DevelopmentSymmetricKey!));
                }
            });

        var authorization = services.AddAuthorizationBuilder();
        foreach (var policy in LedgerPolicies.All)
        {
            authorization.AddPolicy(policy, builder => builder
                .RequireAuthenticatedUser()
                .RequireClaim(LedgerClaims.TenantId)
                .RequireClaim(LedgerClaims.LegalEntityId)
                .RequireAssertion(context => HasScope(context.User, policy)));
        }

        return services;
    }

    private static bool HasScope(System.Security.Claims.ClaimsPrincipal user, string requiredScope)
    {
        foreach (var claim in user.FindAll(LedgerClaims.Scope))
        {
            foreach (var granted in claim.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (string.Equals(granted, requiredScope, StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }

        return false;
    }
}
