namespace Fulfillment.IntegrationTests.Infrastructure;

/// <summary>
/// The SQL Server clock (SYSUTCDATETIME) that all due times, leases, and deadlines use is not
/// monotonic in the Docker Desktop (WSL2) VM: the VM's wall clock runs fast and is stepped back,
/// measured about 0.6 s every 32 s (and about 2 s under load). A row stamped just before a step
/// reads as "in the future" afterwards. Tests therefore never rely on a timestamp the server just
/// wrote, or on an exact boundary, being in the past: they move times by this margin instead.
/// </summary>
public static class SqlClock
{
    public const int MarginSeconds = 30;
}
