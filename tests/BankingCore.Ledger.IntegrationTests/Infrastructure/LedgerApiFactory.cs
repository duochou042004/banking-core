using System.IdentityModel.Tokens.Jwt;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using BankingCore.Ledger.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Tokens;

namespace BankingCore.Ledger.IntegrationTests.Infrastructure;

/// <summary>
/// Hosts the real API in-process against a migrated test database.
/// </summary>
/// <remarks>
/// The host runs with the development symmetric-key authentication profile, which the API refuses to
/// enable in Production. Tokens are minted here so that authorization can be exercised with real
/// validated claims rather than a stubbed authentication handler: the tests then prove the actual
/// policy configuration, not a test double of it.
/// </remarks>
public sealed class LedgerApiFactory : WebApplicationFactory<Program>
{
    private const string Issuer = "banking-core-test";
    private const string Audience = "banking-core-ledger";

    private readonly LedgerDatabaseOptions _database;
    private readonly byte[] _signingKey = RandomNumberGenerator.GetBytes(64);

    /// <summary>Creates the factory bound to a provisioned test database.</summary>
    public LedgerApiFactory(LedgerDatabaseOptions database) => _database = database;

    /// <inheritdoc />
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.UseEnvironment(Environments.Development);

        // UseSetting rather than ConfigureAppConfiguration: with the minimal hosting model the
        // application registers services from builder.Configuration before the factory's
        // configuration callbacks run, so only host settings are visible in time.
        foreach (var (key, value) in new Dictionary<string, string>
        {
            ["Ledger:Database:OwnerConnectionString"] = _database.OwnerConnectionString,
            ["Ledger:Database:PostingConnectionString"] = _database.PostingConnectionString,
            ["Ledger:Database:AdminConnectionString"] = _database.AdminConnectionString,
            ["Ledger:Database:ProjectionConnectionString"] = _database.ProjectionConnectionString,
            ["Ledger:Database:ReadOnlyConnectionString"] = _database.ReadOnlyConnectionString,
            ["Ledger:Authentication:Issuer"] = Issuer,
            ["Ledger:Authentication:Audience"] = Audience,
            ["Ledger:Authentication:DevelopmentSymmetricKey"] = Convert.ToBase64String(_signingKey),
        })
        {
            builder.UseSetting(key, value);
        }
    }

    /// <summary>Creates a client whose requests carry a token for the given scope and grants.</summary>
    public HttpClient CreateClientFor(
        Guid tenantId,
        Guid legalEntityId,
        string subject,
        params string[] scopes)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            MintToken(tenantId, legalEntityId, subject, scopes));
        return client;
    }

    /// <summary>Mints an access token with the given claims.</summary>
    public string MintToken(Guid tenantId, Guid legalEntityId, string subject, params string[] scopes)
    {
        var credentials = new SigningCredentials(
            new SymmetricSecurityKey(_signingKey), SecurityAlgorithms.HmacSha256);

        var claims = new List<Claim>
        {
            new("sub", subject),
            new("actor_type", "workload"),
            new("tenant_id", tenantId.ToString()),
            new("legal_entity_id", legalEntityId.ToString()),
        };

        if (scopes.Length > 0)
        {
            claims.Add(new Claim("scope", string.Join(' ', scopes)));
        }

        var token = new JwtSecurityToken(
            issuer: Issuer,
            audience: Audience,
            claims: claims,
            notBefore: DateTime.UtcNow.AddMinutes(-1),
            expires: DateTime.UtcNow.AddMinutes(10),
            signingCredentials: credentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    /// <summary>A token missing the tenant claim, used to prove the API refuses to guess a scope.</summary>
    public string MintTokenWithoutTenant(string subject, params string[] scopes)
    {
        var credentials = new SigningCredentials(
            new SymmetricSecurityKey(_signingKey), SecurityAlgorithms.HmacSha256);

        var claims = new List<Claim> { new("sub", subject), new("scope", string.Join(' ', scopes)) };
        var token = new JwtSecurityToken(
            issuer: Issuer,
            audience: Audience,
            claims: claims,
            expires: DateTime.UtcNow.AddMinutes(10),
            signingCredentials: credentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    /// <summary>A token signed with the wrong key, used to prove signature validation is on.</summary>
    public static string MintForeignlySignedToken(Guid tenantId, Guid legalEntityId, string subject)
    {
        var credentials = new SigningCredentials(
            new SymmetricSecurityKey(RandomNumberGenerator.GetBytes(64)), SecurityAlgorithms.HmacSha256);

        var token = new JwtSecurityToken(
            issuer: Issuer,
            audience: Audience,
            claims:
            [
                new Claim("sub", subject),
                new Claim("tenant_id", tenantId.ToString()),
                new Claim("legal_entity_id", legalEntityId.ToString()),
                new Claim("scope", "ledger.post ledger.read"),
            ],
            expires: DateTime.UtcNow.AddMinutes(10),
            signingCredentials: credentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
