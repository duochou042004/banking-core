using System.Text.Json;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;

namespace BankingCore.Api;

/// <summary>
/// Turns request-shape failures into deterministic problem responses.
/// </summary>
/// <remarks>
/// A body the framework cannot bind is a client error, not a server fault. Without this handler a
/// wrongly typed field — for example an amount sent as a JSON number where the contract requires a
/// string — surfaces as a 500, which both misreports the fault and risks returning internal detail
/// to the caller (docs/architecture/integration.md, "HTTP APIs"; evaluation AG-012).
/// </remarks>
internal sealed class LedgerRequestExceptionHandler : IExceptionHandler
{
    private const string TypeBase = "https://banking-core.invalid/problems/";

    private readonly IProblemDetailsService _problemDetailsService;

    public LedgerRequestExceptionHandler(IProblemDetailsService problemDetailsService) =>
        _problemDetailsService = problemDetailsService;

    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        if (exception is not (JsonException or BadHttpRequestException))
        {
            // Anything else is a genuine server fault. It is logged and reported as a 500 by the
            // default pipeline, with no internal detail in the response body.
            return false;
        }

        httpContext.Response.StatusCode = StatusCodes.Status400BadRequest;

        return await _problemDetailsService.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            Exception = exception,
            ProblemDetails = new ProblemDetails
            {
                Title = "The request body could not be read.",
                // The exception message is deliberately not echoed: it can quote request content.
                Detail = "The request body does not match the published contract. "
                    + "Check field types; amounts are strings of atomic units, not JSON numbers.",
                Status = StatusCodes.Status400BadRequest,
                Type = TypeBase + "malformed-request",
                Extensions =
                {
                    ["errorCode"] = "malformed-request",
                    ["retryable"] = false,
                },
            },
        });
    }
}
