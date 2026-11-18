using Microsoft.Extensions.DependencyInjection;

namespace Fulfillment.Messaging;

public static class SidecarInvokerRegistration
{
    /// <summary>
    /// Registers <see cref="SidecarInvoker"/> against the local sidecar's HTTP port, sending the Dapr API
    /// token the sidecar requires (DAPR_API_TOKEN).
    /// </summary>
    public static IServiceCollection AddSidecarInvoker(this IServiceCollection services, InvocationRetryPolicy? policy = null)
    {
        var port = Environment.GetEnvironmentVariable("DAPR_HTTP_PORT") ?? "3500";
        var token = Environment.GetEnvironmentVariable("DAPR_API_TOKEN");
        services.AddHttpClient<SidecarInvoker>(client =>
        {
            client.BaseAddress = new Uri($"http://127.0.0.1:{port}/");
            client.Timeout = TimeSpan.FromSeconds(15);
            if (!string.IsNullOrEmpty(token))
            {
                client.DefaultRequestHeaders.Add("dapr-api-token", token);
            }
        }).AddTypedClient(http => new SidecarInvoker(http, policy));
        return services;
    }
}
