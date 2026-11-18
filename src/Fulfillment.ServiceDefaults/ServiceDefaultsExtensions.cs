using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Fulfillment.ServiceDefaults;

/// <summary>Options every host declares when it calls <see cref="ServiceDefaultsExtensions.AddServiceDefaults"/>.</summary>
/// <param name="Component">Role name, equal to the Dapr app id for Dapr-enabled roles.</param>
/// <param name="UsesDaprSidecar">Readiness includes the local sidecar.</param>
/// <param name="DatabaseConnectionName">Connection string name of the owning database (readiness, failpoints), or null.</param>
public sealed record ServiceDefaultsOptions(string Component, bool UsesDaprSidecar, string? DatabaseConnectionName);

/// <summary>What /system/component-identity reports.</summary>
public sealed record RuntimeIdentity(
    string Component,
    string Replica,
    string Version,
    string? Commit,
    string? TreeState,
    string? Branch,
    string? RepositoryUrl,
    string BuildFlavor,
    string Failpoints,
    string Environment);

public static partial class ServiceDefaultsExtensions
{
    public const string LivePath = "/health/live";
    public const string ReadyPath = "/health/ready";
    public const string IdentityPath = "/system/component-identity";

    /// <summary>Header the sidecar sends to the app when APP_API_TOKEN is configured.</summary>
    public const string SidecarTokenHeader = "dapr-api-token";

    /// <summary>Routes only the sidecar may call; requests without the app token are rejected.</summary>
    private static readonly string[] SidecarOnlyPrefixes = ["/internal", "/actors", "/dapr", "/events"];

    public static WebApplicationBuilder AddServiceDefaults(this WebApplicationBuilder builder, ServiceDefaultsOptions options)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(options);

        var identity = CreateIdentity(
            options.Component,
            System.Reflection.Assembly.GetEntryAssembly() ?? typeof(ServiceDefaultsExtensions).Assembly,
            builder.Environment.EnvironmentName);
        builder.Services.AddSingleton(identity);
        builder.Services.AddSingleton(options);
        builder.Services.AddProblemDetails();

        var connectionString = options.DatabaseConnectionName is null
            ? null
            : builder.Configuration.GetConnectionString(options.DatabaseConnectionName)
              ?? throw new InvalidOperationException($"Connection string '{options.DatabaseConnectionName}' is not configured.");
        builder.Services.AddSingleton(sp => new Failpoints(connectionString, sp.GetRequiredService<ILogger<Failpoints>>()));

        var health = builder.Services.AddHealthChecks()
            .AddCheck("self", () => HealthCheckResult.Healthy(), tags: ["live"]);
        if (options.UsesDaprSidecar)
        {
            var sidecarPort = Environment.GetEnvironmentVariable("DAPR_HTTP_PORT") ?? "3500";
            builder.Services.AddHttpClient(DaprSidecarHealthCheck.HttpClientName, c =>
            {
                c.BaseAddress = new Uri($"http://127.0.0.1:{sidecarPort}/");
                c.Timeout = TimeSpan.FromSeconds(2);
            });
            health.AddCheck<DaprSidecarHealthCheck>("dapr-sidecar", tags: ["ready"]);
        }

        if (connectionString is not null)
        {
            health.AddCheck("database", new SqlDatabaseHealthCheck(connectionString), tags: ["ready"]);
        }

        AddTelemetry(builder, identity);
        return builder;
    }

    public static WebApplication MapServiceDefaults(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var identity = app.Services.GetRequiredService<RuntimeIdentity>();
        LogStartup(app.Logger, identity.Component, identity.Replica, identity.Version, identity.Commit ?? "unknown",
            identity.Branch ?? "unknown", identity.BuildFlavor, identity.Failpoints);

        var appToken = Environment.GetEnvironmentVariable("APP_API_TOKEN");
        app.Use(async (context, next) =>
        {
            var path = context.Request.Path.Value ?? string.Empty;
            if (!string.IsNullOrEmpty(appToken)
                && SidecarOnlyPrefixes.Any(p => path.StartsWith(p, StringComparison.Ordinal))
                && !string.Equals(context.Request.Headers[SidecarTokenHeader], appToken, StringComparison.Ordinal))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }

            await next(context);
        });

        app.MapHealthChecks(LivePath, new HealthCheckOptions { Predicate = c => c.Tags.Contains("live") });
        app.MapHealthChecks(ReadyPath, new HealthCheckOptions { Predicate = _ => true });
        app.MapGet(IdentityPath, (RuntimeIdentity id) => Results.Ok(id));
        return app;
    }

    /// <summary>Endpoint filter that requires a role API key in the X-Api-Key header.</summary>
    public static TBuilder RequireApiKey<TBuilder>(this TBuilder builder, string environmentVariable)
        where TBuilder : IEndpointConventionBuilder
    {
        var expected = Environment.GetEnvironmentVariable(environmentVariable);
        builder.AddEndpointFilter(async (context, next) =>
        {
            var provided = context.HttpContext.Request.Headers["X-Api-Key"].ToString();
            if (string.IsNullOrEmpty(expected) || !string.Equals(provided, expected, StringComparison.Ordinal))
            {
                return Results.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "missing or invalid API key");
            }

            return await next(context);
        });
        return builder;
    }

    public static RuntimeIdentity CreateIdentity(string component, System.Reflection.Assembly assembly, string environmentName)
    {
        var build = ComponentIdentity.FromAssembly(assembly);
        return new RuntimeIdentity(
            component,
            Environment.GetEnvironmentVariable("REPLICA_NAME") ?? Environment.MachineName,
            build.Version,
            build.Commit,
            BuildMetadata.Optional("SourceTreeState", assembly),
            build.Branch,
            build.RepositoryUrl,
            BuildMetadata.BuildFlavor,
            Failpoints.Describe(),
            Environment.GetEnvironmentVariable("DAPR_POC_ENV") ?? environmentName);
    }

    private static void AddTelemetry(WebApplicationBuilder builder, RuntimeIdentity identity)
    {
        // Exported only when OTEL_EXPORTER_OTLP_ENDPOINT is set (Compose sets it).
        var export = !string.IsNullOrEmpty(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]);
        var resource = ResourceBuilder.CreateDefault()
            .AddService(identity.Component, serviceVersion: identity.Version, serviceInstanceId: identity.Replica);

        // Logs, traces, and metrics are all exported by the single UseOtlpExporter call below;
        // adding a per-signal AddOtlpExporter as well makes host construction throw.
        builder.Logging.AddOpenTelemetry(o =>
        {
            o.SetResourceBuilder(resource);
            o.IncludeFormattedMessage = true;
            o.IncludeScopes = true;
        });

        var otel = builder.Services.AddOpenTelemetry()
            .ConfigureResource(r => r.AddService(identity.Component, serviceVersion: identity.Version, serviceInstanceId: identity.Replica))
            .WithTracing(t => t
                .AddAspNetCoreInstrumentation(o => o.Filter = ctx => !ctx.Request.Path.StartsWithSegments("/health", StringComparison.Ordinal))
                .AddHttpClientInstrumentation()
                .AddSource("Fulfillment.*"))
            .WithMetrics(m => m
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddRuntimeInstrumentation()
                .AddMeter("Fulfillment.*"));
        if (export)
        {
            otel.UseOtlpExporter();
        }
    }

    [LoggerMessage(Level = LogLevel.Information,
        Message = "starting component={Component} replica={Replica} version={Version} commit={Commit} branch={Branch} flavor={Flavor} failpoints={Failpoints}")]
    private static partial void LogStartup(ILogger logger, string component, string replica, string version, string commit, string branch, string flavor, string failpoints);
}
