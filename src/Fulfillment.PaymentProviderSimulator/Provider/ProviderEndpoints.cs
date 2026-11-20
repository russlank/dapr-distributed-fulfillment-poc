using System.Text.RegularExpressions;
using Fulfillment.Contracts.Payments;
using Fulfillment.ServiceDefaults;
using Microsoft.Data.SqlClient;

namespace Fulfillment.PaymentProviderSimulator.Provider;

public static partial class ProviderEndpoints
{
    private static readonly string[] Kinds = [ProviderKinds.Authorize, ProviderKinds.Capture, ProviderKinds.Void, ProviderKinds.Refund];
    private static readonly string[] FaultModes =
        ["DropResponseAfterCommit", "DelayResponse", "StatusUnknown", "CrashAfterCommit", "Decline", "ExpireAuthorization", "RejectBeforeCommit"];

    [GeneratedRegex("^[\\x21-\\x7e]{1,200}$")]
    private static partial Regex KeyPattern();

    // Read through a field: Failpoints.Compiled is a constant per build flavor.
    private static readonly bool CrashFaultEnabled = Failpoints.Compiled;

    [GeneratedRegex("^[A-Z]{3}$")]
    private static partial Regex CurrencyPattern();

    public static void MapProviderEndpoints(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        var operations = app.MapGroup(ProviderRoutes.Operations).RequireApiKey(ProviderRoutes.ApiKeyVariable);
        operations.MapPost("/", ExecuteAsync);
        operations.MapGet("/{operationKey}", async (string operationKey, ProviderOperations ops, CancellationToken ct) =>
            KeyPattern().IsMatch(operationKey) ? Results.Ok(await ops.QueryAsync(operationKey, ct)) : Results.BadRequest(new { error = "invalid-operation-key" }));

        // Test controls exist only in the drill environment.
        if (Environment.GetEnvironmentVariable("DAPR_POC_ENV") == "drill")
        {
            var test = app.MapGroup("/provider/test").RequireApiKey(ProviderRoutes.ApiKeyVariable);
            test.MapPost("/faults", AddFaultAsync);
            test.MapDelete("/faults", RemoveFaultsAsync);
            test.MapGet("/operations", ListOperationsAsync);
        }
    }

    private static async Task ExecuteAsync(HttpContext http, ProviderOperationRequest request, ProviderOperations ops, ILoggerFactory loggers, CancellationToken ct)
    {
        var error = Validate(request);
        if (error is not null)
        {
            await Results.BadRequest(new { error }).ExecuteAsync(http);
            return;
        }

        var outcome = await ops.ExecuteAsync(request, ct);
        switch (outcome.Kind)
        {
            case ProviderOutcomeKind.PayloadConflict:
                await Results.Conflict(new { error = "operation-key-reused", operationKey = request.OperationKey }).ExecuteAsync(http);
                return;
            case ProviderOutcomeKind.RejectedBeforeCommit:
                await Results.StatusCode(StatusCodes.Status503ServiceUnavailable).ExecuteAsync(http);
                return;
        }

        var logger = loggers.CreateLogger("Fulfillment.PaymentProviderSimulator.Provider");
        Log.Answered(logger, request.OperationKey, request.Kind, outcome.Response!.Status, outcome.Response.Result, outcome.Response.Replayed, outcome.PostCommitFault);
        switch (outcome.PostCommitFault)
        {
            case "DropResponseAfterCommit":
                // Committed; the caller never gets an answer.
                http.Abort();
                return;
            case "DelayResponse":
                await Task.Delay(TimeSpan.FromMilliseconds(outcome.FaultParam ?? 10_000), ct);
                break;
            case "CrashAfterCommit":
                if (CrashFaultEnabled)
                {
                    Environment.FailFast($"provider fault CrashAfterCommit for {request.OperationKey}");
                }

                break;
        }

        await Results.Ok(outcome.Response).ExecuteAsync(http);
    }

    private static string? Validate(ProviderOperationRequest r)
    {
        if (r is null || r.OperationKey is null || !KeyPattern().IsMatch(r.OperationKey))
        {
            return "invalid-operation-key";
        }

        if (!Kinds.Contains(r.Kind))
        {
            return "invalid-kind";
        }

        if (r.AmountMinor < 0 || r.Currency is null || !CurrencyPattern().IsMatch(r.Currency))
        {
            return "invalid-amount";
        }

        return (r.Kind == ProviderKinds.Authorize) == (r.Reference is null) && (r.Reference is null || KeyPattern().IsMatch(r.Reference))
            ? null
            : "invalid-reference";
    }

    /// <param name="Match">An exact operation key, or <c>kind:Authorize</c> (Capture, Void, Refund).</param>
    /// <param name="Param">DelayResponse: milliseconds; ExpireAuthorization: validity in seconds.</param>
    /// <param name="Uses">How many requests (StatusUnknown: queries) it applies to; null: until removed.</param>
    public sealed record FaultRequest(string Label, string Match, string Mode, int? Param, int? Uses);

    private static async Task<IResult> AddFaultAsync(FaultRequest f, IConfiguration configuration, CancellationToken ct)
    {
        if (f is null || string.IsNullOrWhiteSpace(f.Label) || f.Label.Length > 80 || f.Match is null || f.Match.Length > 200 || !FaultModes.Contains(f.Mode))
        {
            return Results.BadRequest(new { error = "invalid-fault" });
        }

        await using var connection = new SqlConnection(configuration.GetConnectionString("PaymentProviderSimDb"));
        await connection.OpenAsync(ct);
        await using var command = new SqlCommand(
            "INSERT provider.Fault (Label, Match, Mode, Param, RemainingUses) OUTPUT inserted.Id VALUES (@l, @m, @mode, @p, @u)", connection);
        command.Parameters.AddWithValue("@l", f.Label);
        command.Parameters.AddWithValue("@m", f.Match);
        command.Parameters.AddWithValue("@mode", f.Mode);
        command.Parameters.AddWithValue("@p", (object?)f.Param ?? DBNull.Value);
        command.Parameters.AddWithValue("@u", (object?)f.Uses ?? DBNull.Value);
        var id = (long)(await command.ExecuteScalarAsync(ct))!;
        return Results.Ok(new { id });
    }

    private static async Task<IResult> RemoveFaultsAsync(string labelPrefix, IConfiguration configuration, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(labelPrefix))
        {
            return Results.BadRequest(new { error = "label-prefix-required" });
        }

        await using var connection = new SqlConnection(configuration.GetConnectionString("PaymentProviderSimDb"));
        await connection.OpenAsync(ct);
        await using var command = new SqlCommand("DELETE provider.Fault WHERE Label LIKE @p + '%'", connection);
        command.Parameters.AddWithValue("@p", labelPrefix);
        return Results.Ok(new { removed = await command.ExecuteNonQueryAsync(ct) });
    }

    /// <summary>Operations, prohibitions, and status-query counts whose key starts with the prefix (drill inspection).</summary>
    private static async Task<IResult> ListOperationsAsync(string prefix, IConfiguration configuration, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(prefix) || prefix.Length < 8)
        {
            return Results.BadRequest(new { error = "prefix-required" });
        }

        await using var connection = new SqlConnection(configuration.GetConnectionString("PaymentProviderSimDb"));
        await connection.OpenAsync(ct);
        var operations = new List<object>();
        await using (var command = new SqlCommand(
                         """
                         SELECT OperationKey, Kind, Result, ResultCode, AmountMinor, Reference, FaultApplied, Replays,
                                (SELECT COUNT(*) FROM provider.StatusQuery q WHERE q.OperationKey = o.OperationKey)
                         FROM provider.Operation o WHERE OperationKey LIKE @p + '%' ORDER BY CommittedAt
                         """, connection))
        {
            command.Parameters.AddWithValue("@p", prefix);
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                operations.Add(new
                {
                    operationKey = reader.GetString(0),
                    kind = reader.GetString(1),
                    result = reader.GetString(2),
                    resultCode = reader.IsDBNull(3) ? null : reader.GetString(3),
                    amountMinor = reader.GetInt64(4),
                    reference = reader.IsDBNull(5) ? null : reader.GetString(5),
                    faultApplied = reader.IsDBNull(6) ? null : reader.GetString(6),
                    replays = reader.GetInt32(7),
                    statusQueries = reader.GetInt32(8),
                });
            }
        }

        return Results.Ok(new { operations });
    }

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Information, Message = "provider {OperationKey} {Kind}: {Status} {Result} (replayed {Replayed}; fault {Fault})")]
        public static partial void Answered(ILogger logger, string operationKey, string kind, string status, string? result, bool replayed, string? fault);
    }
}
