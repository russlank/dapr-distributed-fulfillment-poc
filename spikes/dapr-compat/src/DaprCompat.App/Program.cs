using System.Collections.Concurrent;
using System.Text.Json;
using Dapr.Actors;
using Dapr.Actors.Client;
using Dapr.Client;
using Dapr.Workflow;
using DaprCompat.App;

// One executable, two roles (ROLE=orchestrator | worker), several replicas per role.
// orchestrator: workflows + a local activity; /ctl control API used by the experiment scripts.
// worker: cross-app activities, CounterActor, pub/sub subscriber, flaky invocation target.
var builder = WebApplication.CreateBuilder(args);
var role = Environment.GetEnvironmentVariable("ROLE") ?? "orchestrator";
var connectionString = builder.Configuration["SPIKE_SQL"]
    ?? throw new InvalidOperationException("SPIKE_SQL is not set");
var appToken = Environment.GetEnvironmentVariable("APP_API_TOKEN");

builder.Services.AddSingleton(new Evidence(connectionString));
builder.Services.AddDaprClient();

if (role == "orchestrator")
{
    builder.Services.AddDaprWorkflow(options =>
    {
        options.RegisterWorkflow<CrossAppWorkflow>();
        options.RegisterWorkflow<RecoveryWorkflow>();
        options.RegisterWorkflow<EventWorkflow>();
        options.RegisterWorkflow<TimerWorkflow>();
        options.RegisterWorkflow<FanOutWorkflow>();
        options.RegisterWorkflow<ShortWorkflow>();
        options.RegisterActivity<RecordActivity>();
    });
}
else
{
    builder.Services.AddDaprWorkflow(options =>
    {
        options.RegisterActivity<RecordActivity>();
        options.RegisterActivity<SlowActivity>();
        // Concurrency limits are not set here: WorkflowRuntimeOptions.MaxConcurrentActivities is
        // obsolete and inert in SDK 1.19; the limit is configured on the sidecar (dapr/config).
    });
    builder.Services.AddActors(options => options.Actors.RegisterActor<CounterActor>());
}

var app = builder.Build();
var log = app.Logger;
log.LogInformation("starting role={Role} replica={Replica} appTokenRequired={TokenRequired}",
    role, Replica.Name, !string.IsNullOrEmpty(appToken));

// A12: when APP_API_TOKEN is set, everything except /ctl and /healthz must carry the token the
// sidecar sends (header dapr-api-token). Calls that bypass the sidecar are rejected.
app.Use(async (context, next) =>
{
    var path = context.Request.Path.Value ?? "";
    if (!string.IsNullOrEmpty(appToken) && !path.StartsWith("/ctl", StringComparison.Ordinal) && path != "/healthz"
        && context.Request.Headers["dapr-api-token"] != appToken)
    {
        context.Response.StatusCode = 401;
        return;
    }

    await next();
});

app.MapGet("/healthz", () => Results.Ok(new { role, replica = Replica.Name }));

if (role == "orchestrator")
{
    MapControlApi(app);
}
else
{
    MapWorkerApi(app);
}

app.Run();

static void MapControlApi(WebApplication app)
{
    var inputTypes = new Dictionary<string, Type>
    {
        [nameof(CrossAppWorkflow)] = typeof(string),
        [nameof(RecoveryWorkflow)] = typeof(RecoveryInput),
        [nameof(EventWorkflow)] = typeof(EventInput),
        [nameof(TimerWorkflow)] = typeof(TimerInput),
        [nameof(FanOutWorkflow)] = typeof(FanOutInput),
        [nameof(ShortWorkflow)] = typeof(string),
    };
    var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);

    // Any exception is returned as data: the experiments record what the runtime does.
    static IResult Failure(Exception e) => Results.Json(
        new { ok = false, error = e.GetType().FullName, message = e.Message, inner = e.InnerException?.Message },
        statusCode: 409);

    app.MapPost("/ctl/wf/{name}/{id}", async (string name, string id, HttpRequest request, DaprWorkflowClient client) =>
    {
        try
        {
            var body = await new StreamReader(request.Body).ReadToEndAsync();
            var input = JsonSerializer.Deserialize(body, inputTypes[name], json);
            var instanceId = await client.ScheduleNewWorkflowAsync(name, id, input);
            return Results.Ok(new { ok = true, instanceId, replica = Replica.Name });
        }
        catch (Exception e)
        {
            return Failure(e);
        }
    });

    app.MapGet("/ctl/wf/{id}", async (string id, DaprWorkflowClient client) =>
    {
        try
        {
            var state = await client.GetWorkflowStateAsync(id, getInputsAndOutputs: true);
            if (state is null || !state.Exists)
            {
                return Results.Ok(new { ok = true, exists = false });
            }

            string? output = null, custom = null;
            try { output = state.ReadOutputAs<JsonElement>().ToString(); } catch { }
            try { custom = state.ReadCustomStatusAs<string>(); } catch { }
            return Results.Ok(new
            {
                ok = true,
                exists = true,
                status = state.RuntimeStatus.ToString(),
                createdAt = state.CreatedAt,
                lastUpdatedAt = state.LastUpdatedAt,
                output,
                customStatus = custom,
                failure = state.FailureDetails?.ErrorMessage,
            });
        }
        catch (Exception e)
        {
            return Failure(e);
        }
    });

    app.MapPost("/ctl/wf/{id}/event/{eventName}", async (string id, string eventName, string? value, DaprWorkflowClient client) =>
    {
        try
        {
            await client.RaiseEventAsync(id, eventName, value ?? "v");
            return Results.Ok(new { ok = true });
        }
        catch (Exception e)
        {
            return Failure(e);
        }
    });

    app.MapPost("/ctl/wf/{id}/purge", async (string id, DaprWorkflowClient client) =>
    {
        try
        {
            return Results.Ok(new { ok = true, purged = await client.PurgeInstanceAsync(id) });
        }
        catch (Exception e)
        {
            return Failure(e);
        }
    });

    app.MapPost("/ctl/wf/{id}/terminate", async (string id, DaprWorkflowClient client) =>
    {
        try
        {
            await client.TerminateWorkflowAsync(id);
            return Results.Ok(new { ok = true });
        }
        catch (Exception e)
        {
            return Failure(e);
        }
    });

    app.MapPost("/ctl/actor/{id}/inc", async (string id) =>
    {
        try
        {
            var proxy = ActorProxy.Create(new ActorId(id), nameof(CounterActor));
            var result = await proxy.InvokeMethodAsync<CounterResult>(nameof(CounterActor.IncrementAsync));
            return Results.Ok(new { ok = true, result.Count, result.Replica });
        }
        catch (Exception e)
        {
            return Failure(e);
        }
    });

    app.MapPost("/ctl/pub/{topic}/{messageId}", async (string topic, string messageId, DaprClient dapr) =>
    {
        try
        {
            await dapr.PublishEventAsync("pubsub", topic, new { id = messageId });
            return Results.Ok(new { ok = true });
        }
        catch (Exception e)
        {
            return Failure(e);
        }
    });

    // A11: Dapr service invocation through the sidecar's HTTP API with a plain HttpClient
    // (DaprClient.InvokeMethodAsync is obsolete in SDK 1.19). Retries come from the resiliency policy.
    app.MapPost("/ctl/invoke/{key}", async (string key, int fail) =>
    {
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri("http://localhost:3500") };
            var token = Environment.GetEnvironmentVariable("DAPR_API_TOKEN");
            if (!string.IsNullOrEmpty(token))
            {
                http.DefaultRequestHeaders.Add("dapr-api-token", token);
            }

            using var response = await http.PostAsJsonAsync(
                $"/v1.0/invoke/{Apps.Worker}/method/flaky", new FlakyRequest(key, fail));
            var body = await response.Content.ReadAsStringAsync();
            return Results.Ok(new { ok = response.IsSuccessStatusCode, status = (int)response.StatusCode, body });
        }
        catch (Exception e)
        {
            return Failure(e);
        }
    });
}

static void MapWorkerApi(WebApplication app)
{
    var attempts = new ConcurrentDictionary<string, int>();

    app.MapActorsHandlers();

    // A11: fails with 500 for the first `fail` attempts of each key, then succeeds.
    app.MapPost("/flaky", async (FlakyRequest request, Evidence evidence) =>
    {
        var attempt = attempts.AddOrUpdate($"invoke:{request.Key}", 1, (_, n) => n + 1);
        await evidence.AddAsync("invoke", request.Key, $"attempt={attempt}");
        return attempt <= request.Fail
            ? Results.Problem($"planned failure {attempt}/{request.Fail}", statusCode: 500)
            : Results.Ok($"ok:attempt={attempt}");
    });

    // A10: behaviour by message id prefix: ok-*, retry2-* (fails twice), poison-* (always fails),
    // drop-* (returns DROP).
    app.MapPost("/pubsub/spike", async (HttpRequest request, Evidence evidence) =>
    {
        var id = await ReadCloudEventIdAsync(request);
        var attempt = attempts.AddOrUpdate($"pubsub:{id}", 1, (_, n) => n + 1);
        string outcome;
        IResult result;
        if ((id.StartsWith("retry2-", StringComparison.Ordinal) && attempt <= 2) || id.StartsWith("poison-", StringComparison.Ordinal))
        {
            outcome = "fail-500";
            result = Results.StatusCode(500);
        }
        else if (id.StartsWith("drop-", StringComparison.Ordinal))
        {
            outcome = "DROP";
            result = Results.Ok(new { status = "DROP" });
        }
        else
        {
            outcome = "SUCCESS";
            result = Results.Ok(new { status = "SUCCESS" });
        }

        await evidence.AddAsync("pubsub", id, $"attempt={attempt};outcome={outcome}");
        return result;
    });

    app.MapPost("/pubsub/dlq", async (HttpRequest request, Evidence evidence) =>
    {
        var id = await ReadCloudEventIdAsync(request);
        await evidence.AddAsync("dlq", id, "received");
        return Results.Ok(new { status = "SUCCESS" });
    });

    static async Task<string> ReadCloudEventIdAsync(HttpRequest request)
    {
        using var document = await JsonDocument.ParseAsync(request.Body);
        var root = document.RootElement;
        var data = root.TryGetProperty("data", out var d) ? d : root;
        return data.TryGetProperty("id", out var id) ? id.GetString() ?? "?" : "?";
    }
}

