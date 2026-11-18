using System.CommandLine;
using System.Diagnostics;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;

// External client simulator: ordinary HTTP to the public order API, no Dapr. M0 commands:
// `identity` and `probe` (runtime probe through the admin API). Customer commands (single, load,
// duplicates, approve, inspect) and operator modes arrive with their milestones.

var baseUrl = new Option<Uri>("--base-url") { Description = "Order API base URL", DefaultValueFactory = _ => new Uri("http://127.0.0.1:8080") };
var apiKey = new Option<string?>("--admin-key") { Description = "Admin API key (default: ADMIN_API_KEY environment variable)" };
var probeId = new Option<string>("--probe-id") { Description = "Probe id (letters, digits, '-')", Required = true };
var timer = new Option<int>("--timer") { Description = "Durable timer seconds between the two effects", DefaultValueFactory = _ => 5 };
var timeout = new Option<int>("--timeout") { Description = "Seconds to wait for completion", DefaultValueFactory = _ => 120 };

var identity = new Command("identity", "Print this tool's build identity as JSON");
identity.SetAction(_ =>
{
    var assembly = Assembly.GetExecutingAssembly();
    var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";
    var commit = assembly.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == "SourceRevisionId")?.Value;
    Console.WriteLine(JsonSerializer.Serialize(new { component = "client-simulator", version, commit }));
    return 0;
});

var probe = new Command("probe", "Start a runtime probe and wait for its outcome") { baseUrl, apiKey, probeId, timer, timeout };
probe.SetAction(async (parse, ct) =>
{
    var key = parse.GetValue(apiKey) ?? Environment.GetEnvironmentVariable("ADMIN_API_KEY");
    if (string.IsNullOrEmpty(key))
    {
        Console.Error.WriteLine("admin key missing (--admin-key or ADMIN_API_KEY)");
        return 2;
    }

    using var http = new HttpClient { BaseAddress = parse.GetValue(baseUrl), Timeout = TimeSpan.FromSeconds(10) };
    http.DefaultRequestHeaders.Add("X-Api-Key", key);
    var id = parse.GetValue(probeId)!;
    using var start = await http.PostAsJsonAsync($"/system/probes/runtime/{id}", new { timerSeconds = parse.GetValue(timer) }, ct);
    if (!start.IsSuccessStatusCode)
    {
        Console.Error.WriteLine($"start failed: {(int)start.StatusCode} {await start.Content.ReadAsStringAsync(ct)}");
        return 1;
    }

    var watch = Stopwatch.StartNew();
    while (watch.Elapsed < TimeSpan.FromSeconds(parse.GetValue(timeout)))
    {
        var state = await http.GetFromJsonAsync<JsonObject>($"/system/probes/runtime/{id}", ct);
        var status = state?["workflow"]?["status"]?.GetValue<string>();
        if (status is "Completed" or "Failed" or "Terminated")
        {
            Console.WriteLine(state!.ToJsonString());
            return status == "Completed" ? 0 : 1;
        }

        await Task.Delay(1000, ct);
    }

    Console.Error.WriteLine("timed out waiting for the probe");
    return 1;
});

var root = new RootCommand("Fulfillment client simulator (external; HTTP only)") { identity, probe };
return await root.Parse(args).InvokeAsync();
