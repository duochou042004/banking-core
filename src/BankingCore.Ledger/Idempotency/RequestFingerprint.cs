using System.Security.Cryptography;
using System.Text;

namespace BankingCore.Ledger.Idempotency;

/// <summary>
/// A canonical, collision-resistant digest of the meaningful content of a command.
/// </summary>
/// <remarks>
/// <para>
/// A stored idempotency receipt holds a request fingerprint plus the original terminal outcome. A
/// replay with the same scope and key but a different fingerprint is a conflict, not a retry
/// (docs/architecture/ledger.md, "Idempotency"; evaluation AG-004).
/// </para>
/// <para>
/// The digest is taken over an explicitly built, length-prefixed field sequence rather than over the
/// raw request body, so that insignificant transport differences — key order, whitespace, header
/// casing — do not turn a legitimate retry into a conflict, and so that no restricted value has to
/// be retained to compare later requests.
/// </para>
/// </remarks>
public sealed class RequestFingerprintBuilder
{
    private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

    /// <summary>Appends a UTF-8 string field, length-prefixed so concatenations cannot collide.</summary>
    public RequestFingerprintBuilder Add(string? value)
    {
        if (value is null)
        {
            AddLength(-1);
            return this;
        }

        var bytes = Encoding.UTF8.GetBytes(value);
        AddLength(bytes.Length);
        _hash.AppendData(bytes);
        return this;
    }

    /// <summary>Appends a GUID field.</summary>
    public RequestFingerprintBuilder Add(Guid value)
    {
        Span<byte> buffer = stackalloc byte[16];
        value.TryWriteBytes(buffer);
        AddLength(buffer.Length);
        _hash.AppendData(buffer);
        return this;
    }

    /// <summary>Appends an optional GUID field, distinguishing absent from empty.</summary>
    public RequestFingerprintBuilder Add(Guid? value) => value.HasValue ? Add(value.Value) : Add((string?)null);

    /// <summary>Appends a 64-bit integer field.</summary>
    public RequestFingerprintBuilder Add(long value)
    {
        Span<byte> buffer = stackalloc byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(buffer, value);
        AddLength(buffer.Length);
        _hash.AppendData(buffer);
        return this;
    }

    /// <summary>Appends a date field in ISO 8601 calendar form.</summary>
    public RequestFingerprintBuilder Add(DateOnly value) =>
        Add(value.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));

    /// <summary>Appends an instant field normalised to UTC with fixed precision.</summary>
    public RequestFingerprintBuilder Add(DateTimeOffset value) =>
        Add(value.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ", System.Globalization.CultureInfo.InvariantCulture));

    /// <summary>Finalises the digest. The builder must not be reused afterwards.</summary>
    public byte[] Build() => _hash.GetHashAndReset();

    private void AddLength(int length)
    {
        Span<byte> buffer = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(buffer, length);
        _hash.AppendData(buffer);
    }
}

/// <summary>
/// The scope an idempotency key is unique within.
/// </summary>
/// <remarks>
/// Scoping by tenant, principal, and operation prevents one client's key from colliding with, or
/// resolving to, another client's outcome (docs/architecture/ledger.md, "Idempotency").
/// </remarks>
/// <param name="TenantId">The tenant derived from the authenticated principal.</param>
/// <param name="PrincipalId">The authenticated subject identifier.</param>
/// <param name="Operation">The stable operation name, such as <c>post-internal-transfer</c>.</param>
/// <param name="Key">The client-supplied idempotency key.</param>
public readonly record struct IdempotencyScope(Guid TenantId, string PrincipalId, string Operation, string Key)
{
    /// <summary>The maximum accepted key length.</summary>
    public const int MaxKeyLength = 128;

    /// <summary>Returns a rejection when the scope is structurally invalid.</summary>
    public LedgerError? Validate()
    {
        if (TenantId == Guid.Empty || string.IsNullOrWhiteSpace(PrincipalId) || string.IsNullOrWhiteSpace(Operation))
        {
            return new LedgerError(LedgerErrorCode.MalformedRequest, "The idempotency scope is incomplete.");
        }

        if (string.IsNullOrWhiteSpace(Key) || Key.Length > MaxKeyLength)
        {
            return new LedgerError(
                LedgerErrorCode.MalformedRequest,
                $"An idempotency key is required and must be at most {MaxKeyLength} characters.");
        }

        return null;
    }
}
