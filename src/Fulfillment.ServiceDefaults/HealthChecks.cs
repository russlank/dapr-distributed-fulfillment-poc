using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Fulfillment.ServiceDefaults;

/// <summary>Ready when the local Dapr sidecar reports healthy (GET /v1.0/healthz on its HTTP port).</summary>
public sealed class DaprSidecarHealthCheck(IHttpClientFactory httpClientFactory) : IHealthCheck
{
    public const string HttpClientName = "dapr-sidecar";

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            using var client = httpClientFactory.CreateClient(HttpClientName);
            using var response = await client.GetAsync(new Uri("v1.0/healthz", UriKind.Relative), cancellationToken);
            return response.IsSuccessStatusCode
                ? HealthCheckResult.Healthy()
                : HealthCheckResult.Unhealthy($"sidecar returned {(int)response.StatusCode}");
        }
        catch (HttpRequestException exception)
        {
            return HealthCheckResult.Unhealthy("sidecar unreachable", exception);
        }
    }
}

/// <summary>Ready when the owning database accepts a connection and answers SELECT 1.</summary>
public sealed class SqlDatabaseHealthCheck(string connectionString) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = new SqlCommand("SELECT 1", connection);
            await command.ExecuteScalarAsync(cancellationToken);
            return HealthCheckResult.Healthy();
        }
        catch (SqlException exception)
        {
            return HealthCheckResult.Unhealthy($"database unavailable (error {exception.Number})");
        }
    }
}
