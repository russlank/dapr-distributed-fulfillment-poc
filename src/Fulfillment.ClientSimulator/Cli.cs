using System.CommandLine;
using System.Globalization;
using Fulfillment.ClientSimulator.Http;
using Fulfillment.ClientSimulator.Runs;
using Fulfillment.ClientSimulator.Workload;
using Fulfillment.Contracts.Orders;
using Fulfillment.Contracts.Reviews;

namespace Fulfillment.ClientSimulator;

/// <summary>Command-line surface of the client simulator (M1 customer and operator commands).</summary>
public static class Cli
{
    public const string UrlsVariable = "ORDER_API_URLS";

    public static RootCommand Build(TextWriter? stdout = null, TextWriter? stderr = null)
    {
        var output = stdout ?? Console.Out;
        var errors = stderr ?? Console.Error;
        var root = new RootCommand(
            "Fulfillment client simulator: an external customer and operator client of the order API (HTTP only, no Dapr).\n"
            + "Keys come from the environment only: CUSTOMER_API_KEY, OPERATOR_API_KEY, ADMIN_API_KEY; they are never printed.\n"
            + "Results are JSON on stdout (and --output). Exit codes: 0 ok, 1 expectation failed, 2 usage or missing key,\n"
            + "3 run identity mismatch (new run met an earlier one, or a replay differs), 4 unresolved (no definite answer after retries).")
        {
            Single(output, errors),
            Duplicates(output, errors),
            Conflicts(output, errors),
            Load(output, errors),
            Approve(output, errors),
            Inspect(output, errors),
            LegacyCommands.Identity(),
            LegacyCommands.Probe(),
        };
        return root;
    }

    private sealed class Common
    {
        public Option<string[]> Urls { get; } = new("--url")
        {
            Description = $"Order API base URL; repeat or comma-separate for replicas (default: {UrlsVariable} or http://127.0.0.1:8080)",
            AllowMultipleArgumentsPerToken = false,
        };

        public Option<int?> Replica { get; } = new("--replica") { Description = "Send every request to this replica only (1-based position in the URL list)" };

        public Option<double> RequestTimeout { get; } = new("--request-timeout") { Description = "Seconds per attempt; no answer in time is ambiguous", DefaultValueFactory = _ => 10 };

        public Option<int> MaxAttempts { get; } = new("--max-attempts")
        {
            Description = "Attempts per request; ambiguous answers (timeout, transport error, 408/425/429/5xx) are resent unchanged with the same key",
            DefaultValueFactory = _ => 4,
        };

        public Option<string?> Output { get; } = new("--output") { Description = "Also write the JSON result to this file" };

        public void AddTo(Command command)
        {
            command.Options.Add(Urls);
            command.Options.Add(Replica);
            command.Options.Add(RequestTimeout);
            command.Options.Add(MaxAttempts);
            command.Options.Add(Output);
        }

        /// <summary>The API client from the options, or the reasons it cannot be built.</summary>
        public (ApiClient? Api, IReadOnlyList<string> Problems) Create(ParseResult parse, int concurrency = 16)
        {
            var problems = new List<string>();
            var raw = parse.GetValue(Urls) is { Length: > 0 } given ? given : [Environment.GetEnvironmentVariable(UrlsVariable) ?? "http://127.0.0.1:8080"];
            var urls = new List<Uri>();
            var position = 0;
            foreach (var text in raw.SelectMany(u => u.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)))
            {
                position++;
                if (Uri.TryCreate(text, UriKind.Absolute, out var url) && url.Scheme is "http" or "https" && string.IsNullOrEmpty(url.UserInfo))
                {
                    urls.Add(url);
                }
                else
                {
                    // The value is not echoed: it may carry credentials.
                    problems.Add($"--url (value {position}): an absolute http(s) URL without credentials");
                }
            }

            var replica = parse.GetValue(Replica);
            if (replica is { } r && (r < 1 || r > urls.Count))
            {
                problems.Add($"--replica {r}: 1-{urls.Count}");
            }

            var attempts = parse.GetValue(MaxAttempts);
            if (attempts is < 1 or > 20)
            {
                problems.Add("--max-attempts: 1-20");
            }

            var timeout = parse.GetValue(RequestTimeout);
            if (timeout is <= 0 or > 600 || double.IsNaN(timeout))
            {
                problems.Add("--request-timeout: more than 0, at most 600 seconds");
            }

            if (problems.Count > 0)
            {
                return (null, problems);
            }

#pragma warning disable CA2000 // One client for the process lifetime.
            var http = new HttpClient(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(2), MaxConnectionsPerServer = Math.Max(concurrency, 4) })
            {
                Timeout = Timeout.InfiniteTimeSpan,
            };
#pragma warning restore CA2000
            var endpoints = urls.Select((u, i) => new ApiEndpoint($"replica-{(i + 1).ToString(CultureInfo.InvariantCulture)}", u, http)).ToList();
            var policy = RetryPolicy.Default with { MaxAttempts = attempts, RequestTimeout = TimeSpan.FromSeconds(timeout) };
            return (new ApiClient(endpoints, Credentials.FromEnvironment(), policy), []);
        }
    }

    private sealed class FollowOptionSet
    {
        public Option<bool> Follow { get; } = new("--follow") { Description = "After acceptance, poll the order until it is terminal (Completed, Rejected, Cancelled, Compensated)" };

        public Option<string> Until { get; } = CreateUntil();

        public Option<double> Timeout { get; } = new("--follow-timeout") { Description = "Seconds to follow each order", DefaultValueFactory = _ => 120 };

        public void AddTo(Command command)
        {
            command.Options.Add(Follow);
            command.Options.Add(Until);
            command.Options.Add(Timeout);
        }

        public FollowOptions? Read(ParseResult parse) => parse.GetValue(Follow)
            ? FollowOptions.Default with { Until = parse.GetValue(Until)!, Timeout = TimeSpan.FromSeconds(Math.Clamp(parse.GetValue(Timeout), 1, 3600)) }
            : null;

        private static Option<string> CreateUntil()
        {
            var option = new Option<string>("--follow-until")
            {
                Description = "terminal, or review: also stop when the order waits for an operator (AwaitingApproval)",
                DefaultValueFactory = _ => FollowOptions.Terminal,
            };
            option.AcceptOnlyFromAmong(FollowOptions.Terminal, FollowOptions.Review);
            return option;
        }
    }

    private static Option<string?> RunIdOption(string description) => new("--run-id") { Description = description };

    private static Option<ulong?> SeedOption() => new("--seed") { Description = "Seed of the generated payloads (default 1)" };

    private static Command Single(TextWriter stdout, TextWriter stderr)
    {
        var common = new Common();
        var follow = new FollowOptionSet();
        var runId = RunIdOption("Logical run; the key defaults to <run-id>-o00001 (default: a new run id)");
        var seed = SeedOption();
        var key = new Option<string?>("--key") { Description = "Idempotency key (overrides the run-derived key); resend the same key to replay" };
        var customer = new Option<string?>("--customer") { Description = "Customer id (default: derived from the run)" };
        var lines = new Option<string[]>("--line") { Description = "Order line SKU:QUANTITY; repeat for more lines (default: generated from the seed)" };
        var review = new Option<bool>("--review") { Description = "Generate an order that needs an operator review (value from 1,000.00)" };
        var expect = new Option<string>("--expect") { Description = "new: must create an order; replay: must return an earlier one; any", DefaultValueFactory = _ => RunModes.Any };
        expect.AcceptOnlyFromAmong(RunModes.Any, RunModes.New, RunModes.Replay);
        var command = new Command("single", "Submit one order and optionally follow it") { runId, seed, key, customer, lines, review, expect };
        common.AddTo(command);
        follow.AddTo(command);
        command.SetAction(async (parse, ct) =>
        {
            var problems = new List<string>();
            var run = parse.GetValue(runId) ?? RunIds.New(DateTimeOffset.UtcNow);
            if (!RunIds.IsValid(run))
            {
                problems.Add($"--run-id '{run}': 3-{RunIds.MaxLength} lower-case letters, digits or '-'");
            }

            var spec = new WorkloadSpec(run, parse.GetValue(seed) ?? 1, 1, ReviewRate: parse.GetValue(review) ? 1 : 0);
            OrderIntent? intent = null;
            if (problems.Count == 0)
            {
                var generated = WorkloadGenerator.Intent(spec, 1, StubPrices.ReviewSku(spec.ReviewThresholdMinor));
                var parsedLines = ParseLines(parse.GetValue(lines) ?? [], problems);
                var orderKey = parse.GetValue(key) ?? generated.IdempotencyKey;
                if (orderKey.Length is 0 or > OrderLimits.MaxIdempotencyKeyLength || orderKey.Any(c => c is <= ' ' or > '~'))
                {
                    problems.Add($"--key: 1-{OrderLimits.MaxIdempotencyKeyLength} visible ASCII characters");
                }

                var finalLines = parsedLines.Count > 0 ? parsedLines : generated.Lines;
                intent = WorkloadGenerator.Create(1, orderKey, parse.GetValue(customer) ?? generated.CustomerId, finalLines,
                    StubPrices.TotalMinor(finalLines) >= spec.ReviewThresholdMinor);
            }

            var (api, apiProblems) = common.Create(parse);
            problems.AddRange(apiProblems);
            if (problems.Count > 0 || api is null || intent is null)
            {
                return await UsageAsync(stderr, problems);
            }

            var result = await SingleRun.RunAsync(api, new SingleOptions(intent, run, parse.GetValue(expect)!, parse.GetValue(common.Replica), follow.Read(parse)), ct);
            await ResultJson.WriteAsync(result, api.Credentials.Redactor, parse.GetValue(common.Output), stdout, stderr, ct);
            return result.ExitCode;
        });
        return command;
    }

    private static Command Duplicates(TextWriter stdout, TextWriter stderr)
    {
        var common = new Common();
        var runId = RunIdOption("Logical run (default: a new run id)");
        var seed = SeedOption();
        var orders = new Option<int>("--orders") { Description = "Distinct keys to exercise", DefaultValueFactory = _ => 3 };
        var copies = new Option<int>("--copies") { Description = "Identical submissions per key, sent at once across the replicas", DefaultValueFactory = _ => 4 };
        var noConflict = new Option<bool>("--no-conflict") { Description = "Skip the submission with the same key and a different body" };
        var command = new Command("duplicates", "Send identical submissions concurrently (one order per key) and a conflicting one (422)") { runId, seed, orders, copies, noConflict };
        common.AddTo(command);
        var follow = new FollowOptionSet();
        follow.AddTo(command);
        command.SetAction(async (parse, ct) =>
        {
            var spec = new WorkloadSpec(parse.GetValue(runId) ?? RunIds.New(DateTimeOffset.UtcNow), parse.GetValue(seed) ?? 1, parse.GetValue(orders));
            var problems = new List<string>(spec.Problems());
            if (parse.GetValue(copies) is < 2 or > 32)
            {
                problems.Add("--copies: 2-32");
            }

            var (api, apiProblems) = common.Create(parse, parse.GetValue(copies));
            problems.AddRange(apiProblems);
            if (problems.Count > 0 || api is null)
            {
                return await UsageAsync(stderr, problems);
            }

            var result = await DuplicatesRun.RunAsync(api, new DuplicatesOptions(spec, parse.GetValue(copies), !parse.GetValue(noConflict), parse.GetValue(common.Replica), follow.Read(parse)), ct);
            await ResultJson.WriteAsync(result, api.Credentials.Redactor, parse.GetValue(common.Output), stdout, stderr, ct);
            return result.ExitCode;
        });
        return command;
    }

    private static Command Conflicts(TextWriter stdout, TextWriter stderr)
    {
        var common = new Common();
        var follow = new FollowOptionSet();
        var runId = RunIdOption("Logical run (default: a new run id)");
        var seed = SeedOption();
        var orders = new Option<int>("--orders") { Description = "Keys to exercise", DefaultValueFactory = _ => 3 };
        var variants = new Option<int>("--variants")
        {
            Description = "Distinct bodies per key (customer, quantity, extra line, line order, ...), sent at once across the replicas",
            DefaultValueFactory = _ => 4,
        };
        var command = new Command("conflicts", "Race distinct bodies under one key across the replicas (one accepted, the rest 422), then repeat each on another replica")
        {
            runId, seed, orders, variants,
        };
        common.AddTo(command);
        follow.AddTo(command);
        command.SetAction(async (parse, ct) =>
        {
            var spec = new WorkloadSpec(parse.GetValue(runId) ?? RunIds.New(DateTimeOffset.UtcNow), parse.GetValue(seed) ?? 1, parse.GetValue(orders));
            var problems = new List<string>(spec.Problems());
            if (parse.GetValue(variants) is < 2 or > 16)
            {
                problems.Add("--variants: 2-16");
            }

            var (api, apiProblems) = common.Create(parse, parse.GetValue(variants));
            problems.AddRange(apiProblems);
            if (problems.Count > 0 || api is null)
            {
                return await UsageAsync(stderr, problems);
            }

            var result = await ConflictsRun.RunAsync(api, new ConflictsOptions(spec, parse.GetValue(variants), parse.GetValue(common.Replica), follow.Read(parse)), ct);
            await ResultJson.WriteAsync(result, api.Credentials.Redactor, parse.GetValue(common.Output), stdout, stderr, ct);
            return result.ExitCode;
        });
        return command;
    }

    private static Command Load(TextWriter stdout, TextWriter stderr)
    {
        var common = new Common();
        var follow = new FollowOptionSet();
        var runId = RunIdOption("Logical run; a new run must not exist yet (default: a new run id)");
        var seed = SeedOption();
        var orders = new Option<int?>("--orders") { Description = "Orders in the workload (default 10)" };
        var clients = new Option<int?>("--clients") { Description = "Simulated customers (default 5)" };
        var maxLines = new Option<int?>("--max-lines") { Description = "Lines per ordinary order, 1 to this value (default 3)" };
        var reviewRate = new Option<double?>("--review-rate") { Description = "Share of orders that need an operator review, 0-1 (default 0)" };
        var concurrency = new Option<int>("--concurrency") { Description = "Requests in flight at most", DefaultValueFactory = _ => 4 };
        var rate = new Option<double?>("--rate") { Description = "Started orders per second at most (default: no limit)" };
        var distribution = new Option<string>("--distribution") { Description = "round-robin or seeded replica assignment", DefaultValueFactory = _ => "round-robin" };
        distribution.AcceptOnlyFromAmong("round-robin", "seeded");
        var replay = new Option<bool>("--replay") { Description = "Replay the run given by --run-id/--seed/--orders: every request must return its earlier order" };
        var replayOf = new Option<string?>("--replay-of") { Description = "Replay the run recorded in this result file (workload, digest, and order ids are checked)" };
        var dryRun = new Option<bool>("--dry-run") { Description = "Print the generated requests without sending them" };
        var command = new Command("load", "Send a reproducible workload with bounded concurrency across the replicas")
        {
            runId, seed, orders, clients, maxLines, reviewRate, concurrency, rate, distribution, replay, replayOf, dryRun,
        };
        common.AddTo(command);
        follow.AddTo(command);
        command.SetAction(async (parse, ct) =>
        {
            var problems = new List<string>();
            RecordedRun? original = null;
            WorkloadSpec spec;
            var mode = parse.GetValue(dryRun) ? RunModes.DryRun : parse.GetValue(replay) ? RunModes.Replay : RunModes.New;
            if (parse.GetValue(replayOf) is { } file)
            {
                if (parse.GetValue(runId) is not null || parse.GetValue(seed) is not null || parse.GetValue(orders) is not null || parse.GetValue(clients) is not null
                    || parse.GetValue(maxLines) is not null || parse.GetValue(reviewRate) is not null)
                {
                    problems.Add("--replay-of takes the workload from the file: do not pass --run-id, --seed, --orders, --clients, --max-lines, or --review-rate");
                }

                try
                {
                    original = RecordedRun.Load(file);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidDataException or FormatException)
                {
                    return await UsageAsync(stderr, [$"--replay-of {file}: {e.Message}"]);
                }

                spec = original.Spec;
                mode = parse.GetValue(dryRun) ? RunModes.DryRun : RunModes.Replay;
            }
            else
            {
                if (mode == RunModes.Replay && parse.GetValue(runId) is null)
                {
                    problems.Add("--replay needs the --run-id (and --seed, --orders, ...) of the run to replay");
                }

                spec = new WorkloadSpec(parse.GetValue(runId) ?? RunIds.New(DateTimeOffset.UtcNow), parse.GetValue(seed) ?? 1, parse.GetValue(orders) ?? 10,
                    parse.GetValue(clients) ?? 5, parse.GetValue(maxLines) ?? 3, parse.GetValue(reviewRate) ?? 0);
            }

            problems.AddRange(spec.Problems());
            if (parse.GetValue(concurrency) is < 1 or > 64)
            {
                problems.Add("--concurrency: 1-64");
            }

            if (parse.GetValue(rate) is { } r && (r <= 0 || r > 10_000 || double.IsNaN(r)))
            {
                problems.Add("--rate: more than 0, at most 10000");
            }

            var (api, apiProblems) = common.Create(parse, parse.GetValue(concurrency));
            problems.AddRange(apiProblems);
            if (problems.Count > 0 || api is null)
            {
                return await UsageAsync(stderr, problems);
            }

            var options = new LoadOptions(spec, mode, original, parse.GetValue(concurrency), parse.GetValue(rate),
                parse.GetValue(distribution) == "seeded" ? Distribution.Seeded : Distribution.RoundRobin, parse.GetValue(common.Replica), follow.Read(parse));
            var result = await LoadRun.RunAsync(api, options, ct);
            await ResultJson.WriteAsync(result, api.Credentials.Redactor, parse.GetValue(common.Output), stdout, stderr, ct);
            return result.ExitCode;
        });
        return command;
    }

    private static Command Approve(TextWriter stdout, TextWriter stderr)
    {
        var common = new Common();
        var orders = new Option<Guid[]>("--order") { Description = "Order whose pending review is decided; repeat for more" };
        var reviews = new Option<Guid[]>("--review") { Description = "Review to decide directly; repeat for more" };
        var from = new Option<string?>("--from") { Description = "Decide the pending reviews of every order in this result file" };
        var outcome = new Option<string>("--outcome") { Description = "approve or reject", DefaultValueFactory = _ => DecisionOutcomes.Approve };
        outcome.AcceptOnlyFromAmong(DecisionOutcomes.Approve, DecisionOutcomes.Reject);
        var operatorId = new Option<string>("--operator") { Description = "Simulated operator id (must be configured on the server)", DefaultValueFactory = _ => "operator-1" };
        var comment = new Option<string?>("--comment") { Description = "Optional comment (at most 500 characters)" };
        var wait = new Option<double>("--wait") { Description = "Seconds to wait for an order's review to appear", DefaultValueFactory = _ => 0 };
        var decisionSeed = new Option<string>("--decision-seed")
        {
            Description = "Part of each decision id: the same command decides with the same ids (a rerun is a replay)",
            DefaultValueFactory = _ => "default",
        };
        var command = new Command("approve", "Decide pending reviews through the operator review API (OPERATOR_API_KEY)")
        {
            orders, reviews, from, outcome, operatorId, comment, wait, decisionSeed,
        };
        common.AddTo(command);
        command.SetAction(async (parse, ct) =>
        {
            var problems = new List<string>();
            var orderIds = (parse.GetValue(orders) ?? []).ToList();
            if (parse.GetValue(from) is { } file && !TryReadOrderIds(file, orderIds, problems))
            {
                return await UsageAsync(stderr, problems);
            }

            var reviewIds = parse.GetValue(reviews) ?? [];
            if (orderIds.Count == 0 && reviewIds.Length == 0)
            {
                problems.Add("name at least one --order, --review, or --from file");
            }

            if (parse.GetValue(comment) is { Length: > 500 })
            {
                problems.Add("--comment: at most 500 characters");
            }

            var (api, apiProblems) = common.Create(parse);
            problems.AddRange(apiProblems);
            if (problems.Count > 0 || api is null)
            {
                return await UsageAsync(stderr, problems);
            }

            var options = new ApproveOptions(orderIds, reviewIds, parse.GetValue(outcome)!, parse.GetValue(operatorId)!, parse.GetValue(comment),
                TimeSpan.FromSeconds(Math.Clamp(parse.GetValue(wait), 0, 3600)), parse.GetValue(decisionSeed)!, parse.GetValue(common.Replica));
            var result = await ApproveRun.RunAsync(api, options, ct);
            await ResultJson.WriteAsync(result, api.Credentials.Redactor, parse.GetValue(common.Output), stdout, stderr, ct);
            return result.ExitCode;
        });
        return command;
    }

    private static Command Inspect(TextWriter stdout, TextWriter stderr)
    {
        var common = new Common();
        var orders = new Option<Guid[]>("--order") { Description = "Order to inspect; repeat for more" };
        var from = new Option<string?>("--from") { Description = "Inspect every order in this result file" };
        var command = new Command("inspect", "Show an order's business view, its reviews (OPERATOR_API_KEY), and execution diagnostics (ADMIN_API_KEY)") { orders, from };
        common.AddTo(command);
        command.SetAction(async (parse, ct) =>
        {
            var problems = new List<string>();
            var orderIds = (parse.GetValue(orders) ?? []).ToList();
            if (parse.GetValue(from) is { } file && !TryReadOrderIds(file, orderIds, problems))
            {
                return await UsageAsync(stderr, problems);
            }

            if (orderIds.Count == 0)
            {
                problems.Add("name at least one --order or --from file");
            }

            var (api, apiProblems) = common.Create(parse);
            problems.AddRange(apiProblems);
            if (problems.Count > 0 || api is null)
            {
                return await UsageAsync(stderr, problems);
            }

            var result = await InspectRun.RunAsync(api, orderIds, parse.GetValue(common.Replica), ct);
            await ResultJson.WriteAsync(result, api.Credentials.Redactor, parse.GetValue(common.Output), stdout, stderr, ct);
            return result.ExitCode;
        });
        return command;
    }

    /// <summary>Order lines from SKU:QUANTITY arguments.</summary>
    public static IReadOnlyList<SubmitOrderLine> ParseLines(IEnumerable<string> values, ICollection<string> problems)
    {
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(problems);
        var lines = new List<SubmitOrderLine>();
        foreach (var value in values)
        {
            var parts = value.Split(':');
            if (parts.Length == 2 && parts[0].Length > 0 && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var quantity))
            {
                lines.Add(new SubmitOrderLine(parts[0], quantity));
            }
            else
            {
                problems.Add($"--line '{value}': SKU:QUANTITY");
            }
        }

        return lines;
    }

    private static bool TryReadOrderIds(string file, List<Guid> orderIds, List<string> problems)
    {
        try
        {
            orderIds.AddRange(RecordedRun.OrderIdsIn(file).Where(id => !orderIds.Contains(id)));
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            problems.Add($"--from {file}: {e.Message}");
            return false;
        }
    }

    private static async Task<int> UsageAsync(TextWriter stderr, IEnumerable<string> problems)
    {
        foreach (var problem in problems)
        {
            await stderr.WriteLineAsync("usage: " + problem);
        }

        return ExitCodes.Usage;
    }
}
