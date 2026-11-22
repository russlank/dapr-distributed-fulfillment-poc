using Fulfillment.ServiceDefaults;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;

namespace Fulfillment.InventoryService.Inventory;

/// <summary>
/// Expires holds whose time passed (by SQL time), in every replica but only by the lease holder.
/// Independent of Dapr: no actor reminder, no Scheduler, no Placement (holds keep expiring while
/// those are down). The lease is a SQL row taken with a new token; a replica that lost it (its lease
/// expired and another took it) stops at its next renewal. Expiring a hold is conditional on the
/// hold, so two sweepers or a sweeper racing a release still make one transition.
/// </summary>
public sealed partial class HoldSweeper(InventoryOperations operations, IOptions<InventoryOptions> options, RuntimeIdentity identity,
    string connectionString, ILogger<HoldSweeper> logger) : BackgroundService
{
    public const string LeaseName = "hold-expiry";

    private static readonly System.Diagnostics.Metrics.Meter Meter = new("Fulfillment.Inventory.Sweeper");
    private static volatile int overdue;

    static HoldSweeper()
    {
        // Read by every replica on every pass, whether or not it holds the lease: a sweeper that stopped (or a lease
        // that never expires) shows as holds past their expiry plus the grace.
        Meter.CreateObservableGauge("holds_overdue", () => overdue, description: "holds still Held longer than the grace after their expiry");
    }

    private readonly string owner = $"{identity.Replica}/{Guid.NewGuid():N}"[..Math.Min(identity.Replica.Length + 33, 64)];

    /// <summary>One pass: renew or take the lease; when held, expire up to the batch size. Returns the holds expired.</summary>
    public async Task<int> SweepOnceAsync(CancellationToken ct)
    {
        var settings = options.Value;
        if (!await TryLeaseAsync(settings.SweeperLease, ct))
        {
            return 0;
        }

        var expired = 0;
        while (expired < settings.SweepBatch && await operations.ExpireNextDueAsync(ct) is { } hold)
        {
            expired++;
            LogExpired(logger, hold, identity.Replica);
        }

        return expired;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;
        if (!settings.SweeperEnabled)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SweepOnceAsync(stoppingToken);
                overdue = await CountOverdueAsync(settings.HoldOverdueGrace, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (SqlException e)
            {
                LogSweepFailed(logger, e);
            }

            try
            {
                await Task.Delay(settings.SweepInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task<int> CountOverdueAsync(TimeSpan grace, CancellationToken ct)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var command = new SqlCommand(
            "SELECT COUNT(*) FROM inventory.Hold WHERE State = 'Held' AND ExpiresAt < DATEADD(millisecond, -@ms, SYSUTCDATETIME());", connection);
        command.Parameters.AddWithValue("@ms", (int)grace.TotalMilliseconds);
        return (int)(await command.ExecuteScalarAsync(ct))!;
    }

    /// <summary>Takes the lease when free or expired (by SQL time), or renews it when this sweeper holds it.</summary>
    private async Task<bool> TryLeaseAsync(TimeSpan lease, CancellationToken ct)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var command = new SqlCommand(
            """
            UPDATE inventory.SweeperLease
            SET Owner = @owner, Token = CASE WHEN Owner = @owner THEN Token ELSE NEWID() END,
                ExpiresAt = DATEADD(millisecond, @ms, SYSUTCDATETIME())
            WHERE Name = @name AND (Owner IS NULL OR Owner = @owner OR ExpiresAt <= SYSUTCDATETIME());
            """, connection);
        command.Parameters.AddWithValue("@owner", owner);
        command.Parameters.AddWithValue("@ms", (int)lease.TotalMilliseconds);
        command.Parameters.AddWithValue("@name", LeaseName);
        return await command.ExecuteNonQueryAsync(ct) == 1;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "hold {HoldId} expired by the sweeper on {Replica}")]
    private static partial void LogExpired(ILogger logger, Guid holdId, string replica);

    [LoggerMessage(Level = LogLevel.Warning, Message = "hold sweep failed; retried at the next interval")]
    private static partial void LogSweepFailed(ILogger logger, Exception exception);
}
