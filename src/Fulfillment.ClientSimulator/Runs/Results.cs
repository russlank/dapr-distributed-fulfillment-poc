using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Fulfillment.ClientSimulator.Http;
using Fulfillment.ClientSimulator.Workload;
using Fulfillment.Contracts.Orders;

namespace Fulfillment.ClientSimulator.Runs;

/// <summary>Process exit codes; when several apply, the highest-priority one (lowest row) wins.</summary>
public static class ExitCodes
{
    /// <summary>Every expectation of the command held.</summary>
    public const int Ok = 0;

    /// <summary>A definite answer differed from the expectation (refusal, conflict, order not terminal in time).</summary>
    public const int ExpectationFailed = 1;

    /// <summary>Some request has no definite answer after every retry: its effect is unknown.</summary>
    public const int Unresolved = 4;

    /// <summary>A new run met earlier requests of the same run id, or a replay did not reproduce the original.</summary>
    public const int RunIdentityMismatch = 3;

    /// <summary>Invalid arguments or configuration (also: a required key is not set).</summary>
    public const int Usage = 2;

    private static readonly int[] Priority = [Usage, RunIdentityMismatch, Unresolved, ExpectationFailed];

    public static int Combine(IEnumerable<int> codes)
    {
        var set = codes.ToHashSet();
        return Priority.FirstOrDefault(set.Contains, Ok);
    }

    public static string Describe(int code) => code switch
    {
        Ok => "ok",
        ExpectationFailed => "expectation-failed",
        Usage => "usage",
        RunIdentityMismatch => "run-identity-mismatch",
        Unresolved => "unresolved",
        _ => "unknown",
    };
}

/// <summary>Acceptance of one submitted order intent.</summary>
public static class Acceptances
{
    public const string Created = "created";
    public const string Replayed = "replayed";
    public const string Conflict = "conflict";
    public const string Refused = "refused";
    public const string Unresolved = "unresolved";
    public const string NotSent = "not-sent";
}

public sealed record FollowRecord(string? FinalStatus, bool Reached, IReadOnlyList<string> History, long ElapsedMs, string? Error);

/// <summary>One logical order request: what was sent, every attempt, the answer, and what followed.</summary>
public sealed record SubmissionRecord(
    int Index,
    string IdempotencyKey,
    string CustomerId,
    IReadOnlyList<SubmitOrderLine> Lines,
    string PayloadSha256,
    bool RequiresReview,
    string FirstEndpoint,
    string Acceptance,
    Guid? OrderId,
    int? HttpStatus,
    string? Code,
    IReadOnlyList<AttemptRecord> Attempts)
{
    /// <summary>Null when the expectation held; otherwise why not.</summary>
    public string? Problem { get; init; }

    public int ExitCode { get; init; }

    public FollowRecord? Follow { get; init; }

    /// <summary>The replica that gave the final answer (server-reported), if any.</summary>
    public string? ServedBy => Attempts.Count > 0 ? Attempts[^1].ServedBy : null;

    public bool ResolvedAfterAmbiguous => Attempts.Count > 1 && Acceptance is Acceptances.Created or Acceptances.Replayed
        && Attempts.Any(a => a.Class == AttemptClasses.Ambiguous);
}

public sealed record EndpointInfo(string Name, Uri BaseUrl);

/// <summary>The machine-readable result of one command (written to stdout and optionally a file).</summary>
public sealed record RunResult(
    string Command,
    string Mode,
    string? RunId,
    WorkloadSpec? Workload,
    string? WorkloadDigest,
    IReadOnlyList<EndpointInfo> Endpoints,
    DateTimeOffset StartedAt,
    DateTimeOffset FinishedAt,
    object Summary,
    object Items,
    IReadOnlyList<string> Problems,
    int ExitCode)
{
    public string Tool { get; } = "client-simulator";

    public string Version { get; } = typeof(RunResult).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";

    public string Outcome => ExitCodes.Describe(ExitCode);
}

public static class ResultJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>The result as JSON with every configured secret masked.</summary>
    public static string Render(RunResult result, Redactor redactor)
    {
        ArgumentNullException.ThrowIfNull(redactor);
        return redactor.Redact(JsonSerializer.Serialize(result, Options));
    }

    public static async Task WriteAsync(RunResult result, Redactor redactor, string? outputPath, TextWriter stdout, TextWriter stderr, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(stderr);
        var json = Render(result, redactor);
        if (!string.IsNullOrEmpty(outputPath))
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(outputPath));
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            await File.WriteAllTextAsync(outputPath, json + "\n", ct);
        }

        await stdout.WriteLineAsync(json);
        await stderr.WriteLineAsync(redactor.Redact($"{result.Command} ({result.Mode}{(result.RunId is null ? "" : ", run " + result.RunId)}): {result.Outcome} (exit {result.ExitCode})"));
        foreach (var problem in result.Problems.Take(10))
        {
            await stderr.WriteLineAsync(redactor.Redact("  " + problem));
        }
    }
}

/// <summary>An earlier result read back for a replay: its workload and the order id of each index.</summary>
public sealed record RecordedRun(WorkloadSpec Spec, string Digest, IReadOnlyDictionary<int, Guid> OrderIds)
{
    public static RecordedRun Load(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;
        if (!root.TryGetProperty("workload", out var workload) || workload.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("workloadDigest", out var digest) || digest.ValueKind != JsonValueKind.String)
        {
            throw new InvalidDataException($"{path} is not a load result with a workload (needs 'workload' and 'workloadDigest')");
        }

        var spec = workload.Deserialize<WorkloadSpec>(ResultJson.Options) ?? throw new InvalidDataException($"{path}: unreadable workload");
        var ids = new Dictionary<int, Guid>();
        if (root.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in items.EnumerateArray())
            {
                if (item.TryGetProperty("index", out var index) && item.TryGetProperty("orderId", out var orderId) && orderId.ValueKind == JsonValueKind.String)
                {
                    ids[index.GetInt32()] = orderId.GetGuid();
                }
            }
        }

        return new RecordedRun(spec, digest.GetString()!, ids);
    }

    /// <summary>Order ids recorded in any result file with an "items" array (load, single, duplicates).</summary>
    public static IReadOnlyList<Guid> OrderIdsIn(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var ids = new List<Guid>();
        if (document.RootElement.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in items.EnumerateArray())
            {
                if (item.TryGetProperty("orderId", out var orderId) && orderId.ValueKind == JsonValueKind.String && orderId.TryGetGuid(out var id) && !ids.Contains(id))
                {
                    ids.Add(id);
                }
            }
        }

        return ids;
    }
}
