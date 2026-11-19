using System.CommandLine;
using System.Diagnostics;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Fulfillment.ClientSimulator;

/// <summary>M0 commands: build identity and the runtime probe through the admin API (key from ADMIN_API_KEY only).</summary>
public static class LegacyCommands
{
    public static Command Identity()
    {
        var identity = new Command("identity", "Print this tool's build identity as JSON");
        identity.SetAction(_ =>
        {
            var assembly = Assembly.GetExecutingAssembly();
            var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";
            var commit = assembly.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == "SourceRevisionId")?.Value;
            Console.WriteLine(JsonSerializer.Serialize(new { component = "client-simulator", version, commit }));
            return 0;
        });
        return identity;
    }

    public static Command Probe()
    {
        var baseUrl = new Option<Uri>("--base-url") { Description = "Order API base URL", DefaultValueFactory = _ => new Uri("http://127.0.0.1:8080") };
        var probeId = new Option<string>("--probe-id") { Description = "Probe id (letters, digits, '-')", Required = true };
        var timer = new Option<int>("--timer") { Description = "Durable timer seconds between the two effects", DefaultValueFactory = _ => 5 };
        var timeout = new Option<int>("--timeout") { Description = "Seconds to wait for completion", DefaultValueFactory = _ => 120 };
        var probe = new Command("probe", "Start a runtime probe and wait for its outcome (admin key)") { baseUrl, probeId, timer, timeout };
        probe.SetAction(async (parse, ct) =>
        {
            var key = Environment.GetEnvironmentVariable("ADMIN_API_KEY");
            if (string.IsNullOrEmpty(key))
            {
                Console.Error.WriteLine("admin key missing: set ADMIN_API_KEY");
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
        return probe;
    }
}
