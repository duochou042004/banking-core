using System.Reflection;
using BankingCore.Ledger.Persistence;

namespace BankingCore.ArchitectureTests;

/// <summary>
/// Enforces the module boundaries and financial-safety rules that instructions alone cannot.
/// </summary>
/// <remarks>
/// Deterministic rules belong in tests, analyzers, constraints, or CI rather than in prose
/// (docs/agents/harness.md, "Guidance maintenance"). These assertions fail the build when a change
/// would blur a boundary or introduce binary floating point into financial code.
/// </remarks>
public sealed class ModuleBoundaryTests
{
    private static readonly Assembly Domain = typeof(BankingCore.Ledger.JournalValidator).Assembly;
    private static readonly Assembly Persistence = typeof(SchemaMigrator).Assembly;
    private static readonly Assembly Api = typeof(Program).Assembly;

    private static readonly string[] InfrastructurePrefixes =
    [
        "Npgsql",
        "Microsoft.AspNetCore",
        "Microsoft.Extensions",
        "System.Data.Common",
    ];

    [Fact]
    public void The_domain_kernel_references_no_infrastructure()
    {
        var offenders = Domain.GetReferencedAssemblies()
            .Select(reference => reference.Name ?? string.Empty)
            .Where(name => InfrastructurePrefixes.Any(prefix =>
                name.StartsWith(prefix, StringComparison.Ordinal)))
            .ToArray();

        Assert.True(
            offenders.Length == 0,
            $"{Domain.GetName().Name} must stay free of infrastructure, but references: {string.Join(", ", offenders)}.");
    }

    [Fact]
    public void The_persistence_module_does_not_reference_the_web_host()
    {
        var offenders = Persistence.GetReferencedAssemblies()
            .Select(reference => reference.Name ?? string.Empty)
            .Where(name => name.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal))
            .ToArray();

        Assert.True(
            offenders.Length == 0,
            $"{Persistence.GetName().Name} must not depend on the web host, but references: {string.Join(", ", offenders)}.");
    }

    [Fact]
    public void The_api_does_not_talk_to_the_database_driver_directly()
    {
        // Every database access goes through the persistence module, so the transaction, tenant
        // binding, and retry protocol cannot be bypassed from an endpoint.
        var offenders = Api.GetReferencedAssemblies()
            .Select(reference => reference.Name ?? string.Empty)
            .Where(name => name.StartsWith("Npgsql", StringComparison.Ordinal))
            .ToArray();

        Assert.True(
            offenders.Length == 0,
            $"{Api.GetName().Name} must not reference the database driver directly, but references: "
            + string.Join(", ", offenders));
    }

    [Fact]
    public void No_production_type_exposes_binary_floating_point()
    {
        // docs/architecture/ledger.md, "Value model": float and double are forbidden. This scan
        // covers fields, properties, method parameters, and return types across all production code,
        // not only the types that look monetary.
        var offenders = new List<string>();

        foreach (var assembly in new[] { Domain, Persistence, Api })
        {
            foreach (var type in assembly.GetTypes())
            {
                const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic
                    | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

                foreach (var field in type.GetFields(flags))
                {
                    Record(offenders, field.FieldType, $"{type.FullName}.{field.Name}");
                }

                foreach (var property in type.GetProperties(flags))
                {
                    Record(offenders, property.PropertyType, $"{type.FullName}.{property.Name}");
                }

                foreach (var method in type.GetMethods(flags))
                {
                    Record(offenders, method.ReturnType, $"{type.FullName}.{method.Name} (return)");
                    foreach (var parameter in method.GetParameters())
                    {
                        Record(offenders, parameter.ParameterType, $"{type.FullName}.{method.Name}({parameter.Name})");
                    }
                }
            }
        }

        Assert.True(
            offenders.Count == 0,
            "Binary floating point is forbidden in production code: " + string.Join(", ", offenders));
    }

    [Fact]
    public void Every_migration_is_embedded_exactly_once_and_ordered_by_identifier()
    {
        var identifiers = SchemaMigrator.Migrations.Select(migration => migration.Id).ToArray();

        Assert.NotEmpty(identifiers);
        Assert.Equal(identifiers.Length, identifiers.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(identifiers.OrderBy(id => id, StringComparer.Ordinal), identifiers);
        Assert.All(identifiers, id => Assert.Matches("^[0-9]{4}_[a-z0-9_]+$", id));
    }

    [Fact]
    public void Every_migration_carries_a_sha_256_checksum_over_non_empty_statements()
    {
        Assert.All(SchemaMigrator.Migrations, migration =>
        {
            Assert.Equal(32, migration.Checksum.Length);
            Assert.False(string.IsNullOrWhiteSpace(migration.Sql));
        });
    }

    [Fact]
    public void Every_rejection_code_has_a_distinct_stable_contract_token()
    {
        var codes = Enum.GetValues<BankingCore.Ledger.LedgerErrorCode>();
        var tokens = codes
            .Select(code => new BankingCore.Ledger.LedgerError(code, "probe").Token)
            .ToArray();

        Assert.Equal(codes.Length, tokens.Distinct(StringComparer.Ordinal).Count());
        Assert.All(tokens, token => Assert.Matches("^[a-z0-9]+(-[a-z0-9]+)*$", token));
    }

    private static void Record(List<string> offenders, Type type, string location)
    {
        var candidate = Unwrap(type);
        if (candidate == typeof(float) || candidate == typeof(double))
        {
            offenders.Add(location);
        }

        if (candidate.IsGenericType)
        {
            foreach (var argument in candidate.GetGenericArguments())
            {
                Record(offenders, argument, location);
            }
        }
    }

    private static Type Unwrap(Type type)
    {
        while (type.IsByRef || type.IsArray || type.IsPointer)
        {
            type = type.GetElementType() ?? type;
        }

        return Nullable.GetUnderlyingType(type) ?? type;
    }
}
