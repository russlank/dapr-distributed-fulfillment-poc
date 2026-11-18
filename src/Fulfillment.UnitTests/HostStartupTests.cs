using Fulfillment.ServiceDefaults;
using Microsoft.AspNetCore.Builder;
using Xunit;

namespace Fulfillment.UnitTests;

public class HostStartupTests
{
    // Regression: every host crashed in Compose (OTLP endpoint set) because a per-signal OTLP
    // exporter was combined with UseOtlpExporter; the unit tests had built no host at all.
    [Theory]
    [InlineData(null)]
    [InlineData("http://otel-collector:4317")]
    public async Task AddServiceDefaults_BuildsHost_WithAndWithoutOtlpEndpoint(string? otlpEndpoint)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"] = otlpEndpoint;
        builder.Configuration["ConnectionStrings:TestDb"] = "Server=127.0.0.1,1;Database=x;User Id=x;Password=x;TrustServerCertificate=true";
        builder.AddServiceDefaults(new ServiceDefaultsOptions("test", UsesDaprSidecar: true, DatabaseConnectionName: "TestDb"));

        await using var app = builder.Build();
        app.MapServiceDefaults();

        Assert.NotNull(app.Services.GetService(typeof(RuntimeIdentity)));
    }
}
