using System.Diagnostics.Metrics;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;

namespace Fulfillment.OrderApi.Operations;

public sealed class OperationalGaugeOptions
{
    public const string Section = "OperationalGauges";

    /// <summary>How often OrderDb is read for the operational gauges.</summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>A pending start request, signal, or carrier forward older than this counts as aging.</summary>
    public TimeSpan BacklogAge { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>A projection gap whose oldest pending event is older than this counts as aging.</summary>
    public TimeSpan GapAge { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Dead letters recorded within this window are reported (so the alert resolves once it passes).</summary>
    public TimeSpan DeadLetterWindow { get; set; } = TimeSpan.FromMinutes(30);

    public static void ForEnvironment(OperationalGaugeOptions options, string? environment)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (environment == "drill")
        {
            options.Interval = TimeSpan.FromSeconds(5);
            options.BacklogAge = TimeSpan.FromSeconds(15);
            options.GapAge = TimeSpan.FromSeconds(5);
            options.DeadLetterWindow = TimeSpan.FromSeconds(90);
        }
    }
}

/// <summary>
/// Operational state of OrderDb as metrics, for dashboards and alerts: aging backlogs of the leased work queues
/// (start requests, pending signals by kind, carrier forwards), recent dead letters, aging projection gaps, and active
/// intervention cases by kind and state. Thresholds are applied here (per time profile), so the alert rules only ask
/// for a value above zero. Read every interval by each order API replica; the rules take the maximum across replicas.
/// No ids appear as labels.
/// </summary>
public sealed partial class OperationalGauges(string connectionString, IOptions<OperationalGaugeOptions> options, ILogger<OperationalGauges> logger)
    : BackgroundService
{
    private static readonly Meter Meter = new("Fulfillment.Operations");
    private static volatile Snapshot current = Snapshot.Empty;

    static OperationalGauges()
    {
        Meter.CreateObservableGauge("start_requests_aging", () => current.StartRequests, description: "pending start requests older than the backlog age");
        Meter.CreateObservableGauge("pending_signals_aging", () => current.Signals.Select(s => new Measurement<int>(s.Value, new KeyValuePair<string, object?>("kind", s.Key))),
            description: "pending workflow signals older than the backlog age, by kind");
        Meter.CreateObservableGauge("carrier_forwards_aging", () => current.Forwards, description: "pending carrier forwards older than the backlog age");
        Meter.CreateObservableGauge("event_dead_letters_recent", () => current.DeadLetters, description: "event dead letters recorded within the window");
        Meter.CreateObservableGauge("projection_gaps_aging", () => current.Gaps, description: "projection gaps whose oldest pending event is older than the gap age");
        Meter.CreateObservableGauge("intervention_cases_active", () => current.Cases.Select(c => new Measurement<int>(c.Count,
            new KeyValuePair<string, object?>("kind", c.Kind), new KeyValuePair<string, object?>("state", c.State))),
            description: "active intervention cases (Open, Reconciling, Escalated) by kind and state");
    }

    private sealed record Snapshot(int StartRequests, IReadOnlyDictionary<string, int> Signals, int Forwards, int DeadLetters, int Gaps,
        IReadOnlyList<(string Kind, string State, int Count)> Cases)
    {
        public static readonly Snapshot Empty = new(0, new Dictionary<string, int>(), 0, 0, 0, []);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                current = await ReadAsync(stoppingToken);
            }
            catch (SqlException e)
            {
                LogReadFailed(logger, e.Number);
            }

            try
            {
                await Task.Delay(options.Value.Interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task<Snapshot> ReadAsync(CancellationToken ct)
    {
        var o = options.Value;
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var command = new SqlCommand(
            """
            DECLARE @now datetime2(3) = SYSUTCDATETIME();
            SELECT
              (SELECT COUNT(*) FROM orders.WorkflowStartRequest WHERE State = 'Pending' AND CreatedAt < DATEADD(millisecond, -@backlog, @now)),
              (SELECT COUNT(*) FROM orders.CarrierStatusForward WHERE State = 'Pending' AND CreatedAt < DATEADD(millisecond, -@backlog, @now)),
              (SELECT COUNT(*) FROM orders.EventDeadLetter WHERE RecordedAt > DATEADD(millisecond, -@dead, @now)),
              (SELECT COUNT(*) FROM orders.OrderEventGaps WHERE OldestPendingAt < DATEADD(millisecond, -@gap, @now));
            SELECT CASE WHEN EventName LIKE 'ReviewSignal[_]%' THEN 'review' WHEN EventName LIKE 'InterventionResolved[_]%' THEN 'intervention'
                        WHEN EventName LIKE 'CarrierStatus[_]%' THEN 'carrier' ELSE 'other' END, COUNT(*)
            FROM orders.PendingSignal WHERE State = 'Pending' AND CreatedAt < DATEADD(millisecond, -@backlog, @now)
            GROUP BY CASE WHEN EventName LIKE 'ReviewSignal[_]%' THEN 'review' WHEN EventName LIKE 'InterventionResolved[_]%' THEN 'intervention'
                          WHEN EventName LIKE 'CarrierStatus[_]%' THEN 'carrier' ELSE 'other' END;
            SELECT Kind, State, COUNT(*) FROM orders.InterventionCase WHERE State IN ('Open', 'Reconciling', 'Escalated') GROUP BY Kind, State;
            """, connection);
        command.Parameters.AddWithValue("@backlog", (int)o.BacklogAge.TotalMilliseconds);
        command.Parameters.AddWithValue("@dead", (int)o.DeadLetterWindow.TotalMilliseconds);
        command.Parameters.AddWithValue("@gap", (int)o.GapAge.TotalMilliseconds);
        await using var reader = await command.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        var (starts, forwards, dead, gaps) = (reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2), reader.GetInt32(3));

        // Every kind is reported (zero when none), so a backlog that drains shows 0 rather than a stale value.
        var signals = new Dictionary<string, int> { ["review"] = 0, ["intervention"] = 0, ["carrier"] = 0, ["other"] = 0 };
        await reader.NextResultAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            signals[reader.GetString(0)] = reader.GetInt32(1);
        }

        var cases = new List<(string, string, int)>();
        await reader.NextResultAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            cases.Add((reader.GetString(0), reader.GetString(1), reader.GetInt32(2)));
        }

        // A kind that had active cases and has none now is reported as 0 once, so its series ends at zero.
        foreach (var gone in current.Cases.Where(c => c.Count > 0 && !cases.Any(n => n.Item1 == c.Kind && n.Item2 == c.State)))
        {
            cases.Add((gone.Kind, gone.State, 0));
        }

        return new Snapshot(starts, signals, forwards, dead, gaps, cases);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "operational gauges: OrderDb read failed (SQL error {Number}); the last values stay")]
    private static partial void LogReadFailed(ILogger logger, int number);
}
