using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Fulfillment.Contracts.Orders;

namespace Fulfillment.ClientSimulator.Workload;

/// <summary>
/// Names of a logical run. Every idempotency key and customer id of a run starts with its run id, so
/// the same run id and seed reproduce the same logical requests, and a new run id gives new orders.
/// </summary>
public static partial class RunIds
{
    public const int MaxLength = 40;

    public static bool IsValid(string? runId) => runId is not null && Pattern().IsMatch(runId);

    /// <summary>A fresh run id: UTC time plus 16 random bits, for a new independent run.</summary>
    public static string New(DateTimeOffset now) =>
        $"cs-{now.UtcDateTime.ToString("yyyyMMdd'-'HHmmss", CultureInfo.InvariantCulture)}-{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(2))}";

    public static string IdempotencyKey(string runId, int index) => $"{runId}-o{index.ToString("D5", CultureInfo.InvariantCulture)}";

    public static string Customer(string runId, int client) => $"{runId}-c{client.ToString("D2", CultureInfo.InvariantCulture)}";

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{2,39}$")]
    private static partial Regex Pattern();
}

/// <summary>Everything that determines a workload; recorded in every result so a run can be replayed.</summary>
/// <param name="RunId">Logical run (prefix of keys and customer ids).</param>
/// <param name="Seed">Seed of the payload generator.</param>
/// <param name="Orders">Number of orders (1-based indexes).</param>
/// <param name="Clients">Size of the simulated customer pool.</param>
/// <param name="MaxLines">Lines per ordinary order: 1 to this value, distinct SKUs.</param>
/// <param name="ReviewRate">Share of orders that need an operator review (0 to 1).</param>
/// <param name="CatalogSize">Ordinary SKUs SKU-1 .. SKU-n.</param>
/// <param name="ReviewThresholdMinor">Order value from which the server requires a review (stub price list).</param>
/// <param name="FixedSku">Contention workloads: every order has one line of this SKU (the review draw and line generation are skipped).</param>
/// <param name="FixedQuantity">Units per order of <paramref name="FixedSku"/>.</param>
public sealed partial record WorkloadSpec(
    string RunId,
    ulong Seed,
    int Orders,
    int Clients = 5,
    int MaxLines = 3,
    double ReviewRate = 0,
    int CatalogSize = 20,
    long ReviewThresholdMinor = StubPrices.DefaultReviewThresholdMinor,
    string? FixedSku = null,
    int FixedQuantity = 1)
{
    public const int MaxOrders = 10_000;

    /// <summary>Contract problems of the spec, or an empty list.</summary>
    public IReadOnlyList<string> Problems()
    {
        var problems = new List<string>();
        if (!RunIds.IsValid(RunId))
        {
            problems.Add($"run id '{RunId}': 3-{RunIds.MaxLength} characters, lower-case letters, digits or '-', starting with a letter or digit");
        }

        if (Orders is < 1 or > MaxOrders)
        {
            problems.Add($"orders: 1-{MaxOrders}");
        }

        if (Clients is < 1 or > 99)
        {
            problems.Add("clients: 1-99");
        }

        if (CatalogSize is < 1 or > 999)
        {
            problems.Add("catalog size: 1-999");
        }

        if (MaxLines < 1 || MaxLines > Math.Min(CatalogSize, OrderLimits.MaxLines))
        {
            problems.Add($"max lines: 1-{Math.Min(CatalogSize, OrderLimits.MaxLines)} (distinct SKUs per order)");
        }

        if (ReviewRate is < 0 or > 1 || double.IsNaN(ReviewRate))
        {
            problems.Add("review rate: 0 to 1");
        }

        if (ReviewThresholdMinor < 1)
        {
            problems.Add("review threshold: positive minor units");
        }

        if (FixedSku is not null && !SkuPattern().IsMatch(FixedSku))
        {
            problems.Add($"sku '{FixedSku}': 1-{OrderLimits.MaxSkuLength} upper-case letters, digits or '-', starting with a letter or digit");
        }

        if (FixedQuantity < OrderLimits.MinQuantity || FixedQuantity > OrderLimits.MaxQuantity)
        {
            problems.Add($"quantity: {OrderLimits.MinQuantity}-{OrderLimits.MaxQuantity}");
        }

        return problems;
    }
}

/// <summary>One logical request of a workload: the same spec always yields the same intents.</summary>
/// <param name="Index">1-based position in the workload.</param>
/// <param name="IdempotencyKey">Stable key of the logical request.</param>
/// <param name="Body">The exact JSON body that is sent (UTF-8).</param>
/// <param name="PayloadSha256">Hex SHA-256 of <paramref name="Body"/>.</param>
/// <param name="RequiresReview">True when the order value reaches the review threshold.</param>
public sealed record OrderIntent(int Index, string IdempotencyKey, string CustomerId, IReadOnlyList<SubmitOrderLine> Lines, string Body, string PayloadSha256, bool RequiresReview);

/// <summary>Builds the intents of a workload from its spec. Pure and deterministic.</summary>
public static class WorkloadGenerator
{
    public static IReadOnlyList<OrderIntent> Generate(WorkloadSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        var problems = spec.Problems();
        if (problems.Count > 0)
        {
            throw new ArgumentException(string.Join("; ", problems), nameof(spec));
        }

        var reviewSku = StubPrices.ReviewSku(spec.ReviewThresholdMinor);
        return [.. Enumerable.Range(1, spec.Orders).Select(i => Intent(spec, i, reviewSku))];
    }

    /// <summary>One intent; independent of the other indexes (each has its own random stream).</summary>
    public static OrderIntent Intent(WorkloadSpec spec, int index, string reviewSku)
    {
        ArgumentNullException.ThrowIfNull(spec);
        var random = DeterministicRandom.ForItem(spec.Seed, index);
        var customer = RunIds.Customer(spec.RunId, 1 + random.Next(spec.Clients));
        if (spec.FixedSku is { } sku)
        {
            IReadOnlyList<SubmitOrderLine> one = [new SubmitOrderLine(sku, spec.FixedQuantity)];
            return Create(index, RunIds.IdempotencyKey(spec.RunId, index), customer, one, StubPrices.TotalMinor(one) >= spec.ReviewThresholdMinor);
        }

        var review = random.NextDouble() < spec.ReviewRate;
        IReadOnlyList<SubmitOrderLine> lines;
        if (review)
        {
            lines = [new SubmitOrderLine(reviewSku, StubPrices.ReviewQuantity)];
        }
        else
        {
            // Distinct SKUs (one line per SKU is an M1 business rule), 1-3 units each: with the default
            // of at most 3 lines the value stays below 1,000.00 (3 x 3 x 99.99). RequiresReview below
            // is computed from the prices either way.
            var catalog = Enumerable.Range(1, spec.CatalogSize).ToArray();
            var count = 1 + random.Next(spec.MaxLines);
            for (var k = 0; k < count; k++)
            {
                var j = k + random.Next(catalog.Length - k);
                (catalog[k], catalog[j]) = (catalog[j], catalog[k]);
            }

            lines = [.. catalog.Take(count).Select(n => new SubmitOrderLine($"SKU-{n.ToString(CultureInfo.InvariantCulture)}", 1 + random.Next(3)))];
        }

        return Create(index, RunIds.IdempotencyKey(spec.RunId, index), customer, lines, StubPrices.TotalMinor(lines) >= spec.ReviewThresholdMinor);
    }

    public static OrderIntent Create(int index, string key, string customerId, IReadOnlyList<SubmitOrderLine> lines, bool requiresReview)
    {
        var body = Body(customerId, lines);
        return new OrderIntent(index, key, customerId, lines, body, Sha256Hex(body), requiresReview);
    }

    /// <summary>The request body, property order fixed: customerId, lines[sku, quantity].</summary>
    public static string Body(string customerId, IReadOnlyList<SubmitOrderLine> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("customerId", customerId);
            writer.WriteStartArray("lines");
            foreach (var line in lines)
            {
                writer.WriteStartObject();
                writer.WriteString("sku", line.Sku);
                writer.WriteNumber("quantity", line.Quantity);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>Digest of a whole workload (keys and bodies in order): equal digests, equal logical requests.</summary>
    public static string Digest(IEnumerable<OrderIntent> intents)
    {
        ArgumentNullException.ThrowIfNull(intents);
        var text = new StringBuilder();
        foreach (var intent in intents)
        {
            text.Append(intent.IdempotencyKey).Append('\n').Append(intent.Body).Append('\n');
        }

        return Sha256Hex(text.ToString());
    }

    public static string Sha256Hex(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}

/// <summary>
/// The server's stub price list (FNV-1a of the SKU, 1.00 to 99.99), mirrored to generate orders that
/// do or do not need a review. A unit test keeps it equal to the compute worker's list.
/// </summary>
public static class StubPrices
{
    public const long DefaultReviewThresholdMinor = 100_000;
    public const int ReviewQuantity = 100;

    public static long UnitPriceMinor(string sku)
    {
        ArgumentNullException.ThrowIfNull(sku);
        var hash = 2166136261u;
        foreach (var c in sku)
        {
            hash = unchecked((hash ^ c) * 16777619u);
        }

        return 100 + (hash % 9900);
    }

    public static long TotalMinor(IEnumerable<SubmitOrderLine> lines) => lines.Sum(l => UnitPriceMinor(l.Sku) * l.Quantity);

    /// <summary>The first SKU-R&lt;n&gt; whose <see cref="ReviewQuantity"/> units reach the threshold.</summary>
    public static string ReviewSku(long thresholdMinor)
    {
        for (var n = 1; n < 100_000; n++)
        {
            var sku = $"SKU-R{n.ToString(CultureInfo.InvariantCulture)}";
            if (UnitPriceMinor(sku) * ReviewQuantity >= thresholdMinor)
            {
                return sku;
            }
        }

        throw new ArgumentOutOfRangeException(nameof(thresholdMinor), "no review SKU reaches the threshold");
    }
}

public sealed partial record WorkloadSpec
{
    [GeneratedRegex("^[A-Z0-9][A-Z0-9-]{0,31}$")]
    private static partial Regex SkuPattern();
}
