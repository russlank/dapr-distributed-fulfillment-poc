using Dapr.Workflow;
using Fulfillment.Contracts;
using Fulfillment.Contracts.Payments;
using Fulfillment.Contracts.Workflows;
using Fulfillment.PaymentService.Payments;
using Fulfillment.PaymentService.Persistence;
using Fulfillment.ServiceDefaults;

if (PaymentDbMigrations.IsMigrateCommand(args))
{
    return await PaymentDbMigrations.RunAsync(args);
}

// Dapr app payment-service: owns PaymentDb; runs payment operations against the external provider by
// operation key (intent first, reconciliation by status query, never a new key to try again).
var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults(new ServiceDefaultsOptions(AppIds.PaymentService, UsesDaprSidecar: true, DatabaseConnectionName: "PaymentDb"));

var paymentDb = builder.Configuration.GetConnectionString("PaymentDb")!;
var paymentOptions = new PaymentOptions();
PaymentOptions.ForEnvironment(paymentOptions, Environment.GetEnvironmentVariable("DAPR_POC_ENV"));
builder.Configuration.GetSection(PaymentOptions.Section).Bind(paymentOptions);
builder.Services.AddSingleton(paymentOptions);
builder.Services.AddHttpClient<ProviderClient>(http =>
{
    http.BaseAddress = paymentOptions.ProviderUrl;
    // Per-call timeouts are set by the client; this only bounds a stuck connection.
    http.Timeout = TimeSpan.FromMinutes(1);
    http.DefaultRequestHeaders.Add("X-Api-Key", Environment.GetEnvironmentVariable(ProviderRoutes.ApiKeyVariable) ?? "");
}).ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
{
    // Connecting has its own, shorter limit: a connect that times out sent nothing (classified Refused), while a
    // request timeout after the connection was made may have committed (classified Timeout).
    ConnectTimeout = paymentOptions.RequestTimeout / 2,
});
builder.Services.AddSingleton(sp => new PaymentExecutor(paymentDb, sp.GetRequiredService<ProviderClient>(), paymentOptions,
    sp.GetRequiredService<RuntimeIdentity>().Replica, sp.GetRequiredService<ILogger<PaymentExecutor>>()));
builder.Services.AddDaprWorkflow(options =>
{
    options.RegisterActivity<ExecutePaymentOperationActivity>(PaymentActivities.ExecutePayment);
    options.RegisterActivity<ReconcilePaymentOperationActivity>(PaymentActivities.ReconcilePayment);
});

var app = builder.Build();
await PaymentDbMigrations.EnsureCurrentAsync(paymentDb);
app.MapServiceDefaults();
await app.RunAsync();
return 0;
