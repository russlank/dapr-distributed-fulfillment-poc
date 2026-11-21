using Fulfillment.Contracts;
using Fulfillment.Contracts.Events;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Fulfillment.Messaging.Outbox;

public static class OutboxRegistration
{
    /// <summary>Dapr sets this header on service invocation; the outbox endpoints answer only the order API (its reconciler).</summary>
    public const string CallerHeader = "dapr-caller-app-id";

    /// <summary>
    /// Registers the source's outbox store and a relay (one per replica; relays share the table through leases).
    /// <paramref name="checkpoint"/> is the failpoint hook (a no-op in release builds).
    /// </summary>
    public static IServiceCollection AddOutboxRelay(this IServiceCollection services, IConfiguration configuration, string connectionString, OutboxTable table,
        Func<IServiceProvider, Func<string, string, CancellationToken, Task>> checkpoint)
    {
        ArgumentNullException.ThrowIfNull(table);
        var replica = Environment.GetEnvironmentVariable("REPLICA_NAME") ?? Environment.MachineName;
        var options = new OutboxRelayOptions();
        OutboxRelayOptions.ForEnvironment(options, Environment.GetEnvironmentVariable("DAPR_POC_ENV"));
        configuration.GetSection(OutboxRelayOptions.Section).Bind(options);
        services.AddSingleton(options);
        services.AddSingleton(new OutboxStore(connectionString, table));
        var port = Environment.GetEnvironmentVariable("DAPR_HTTP_PORT") ?? "3500";
        var token = Environment.GetEnvironmentVariable("DAPR_API_TOKEN");
        services.AddHttpClient<DaprEventPublisher>(client =>
        {
            client.BaseAddress = new Uri($"http://127.0.0.1:{port}/");
            client.Timeout = TimeSpan.FromSeconds(30);
            if (!string.IsNullOrEmpty(token))
            {
                client.DefaultRequestHeaders.Add("dapr-api-token", token);
            }
        });
        // A new owner id per process: a restarted relay does not mistake an old lease for its own.
        var owner = $"{replica[..Math.Min(replica.Length, 31)]}/{Guid.NewGuid():N}";
        services.AddHostedService(sp => new OutboxRelay(sp.GetRequiredService<OutboxStore>(), sp.GetRequiredService<DaprEventPublisher>(), options, owner,
            checkpoint(sp), sp.GetRequiredService<ILogger<OutboxRelay>>()));
        return services;
    }

    /// <summary>
    /// <c>POST internal/outbox/replay</c> (republish an order's rows from a sequence) and
    /// <c>GET internal/outbox/heads?since=&amp;settledBefore=</c> (highest settled published sequence per recent order), for the order API's
    /// reconciler only.
    /// </summary>
    public static IEndpointRouteBuilder MapOutboxEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/" + OutboxRoutes.Replay, async (HttpContext http, OutboxReplayRequest request, OutboxStore store, CancellationToken ct) =>
        {
            if (!FromOrderApi(http))
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            return request.FromSequence < 1
                ? Results.BadRequest(new { errorCode = "invalid-sequence" })
                : Results.Ok(await store.ReplayAsync(request.OrderId, request.FromSequence, ct));
        });
        endpoints.MapGet("/" + OutboxRoutes.Heads, async (HttpContext http, DateTime since, DateTime settledBefore, OutboxStore store, CancellationToken ct) =>
            FromOrderApi(http)
                ? Results.Ok(await store.HeadsAsync(DateTime.SpecifyKind(since, DateTimeKind.Utc), DateTime.SpecifyKind(settledBefore, DateTimeKind.Utc), ct))
                : Results.StatusCode(StatusCodes.Status403Forbidden));
        return endpoints;
    }

    private static bool FromOrderApi(HttpContext http) => http.Request.Headers[CallerHeader].ToString() == AppIds.OrderApi;
}
