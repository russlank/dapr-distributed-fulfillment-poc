using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Fulfillment.Contracts.Orders;

namespace Fulfillment.OrderApi.Orders;

/// <summary>
/// Contract checks of a submission (syntax and bounds only; business rules belong to the workflow's
/// validation) and the request hash that binds an idempotency key to its payload.
/// </summary>
public static partial class OrderRequests
{
    /// <summary>
    /// Strict reading: unknown, duplicate, missing, or null members are contract violations. This keeps
    /// the hash meaningful: two bodies that parse to the same request are the same request.
    /// </summary>
    public static readonly JsonSerializerOptions ReadOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
        AllowDuplicateProperties = false,
        PropertyNameCaseInsensitive = false,
    };

    private static readonly JsonSerializerOptions HashOptions = new(JsonSerializerDefaults.Web) { WriteIndented = false };

    /// <summary>Parses a body; null with a JSON path (never the input text) when it is not a valid document.</summary>
    public static SubmitOrderRequest? TryParse(ReadOnlySpan<byte> body, out string? errorPath)
    {
        try
        {
            var request = JsonSerializer.Deserialize<SubmitOrderRequest>(body, ReadOptions);
            errorPath = request is null ? "$" : null;
            return request;
        }
        catch (JsonException e)
        {
            errorPath = e.Path ?? "$";
            return null;
        }
    }

    /// <summary>Contract violations by field; empty when the request is acceptable. Messages never quote values.</summary>
    public static Dictionary<string, string[]> Validate(SubmitOrderRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (!CustomerIdPattern().IsMatch(request.CustomerId))
        {
            errors["customerId"] = [$"1-{OrderLimits.MaxCustomerIdLength} characters: letters, digits, '.', '_' or '-'"];
        }

        if (request.Lines.Count is 0 or > OrderLimits.MaxLines)
        {
            errors["lines"] = [$"1-{OrderLimits.MaxLines} lines"];
        }

        for (var i = 0; i < request.Lines.Count && i < OrderLimits.MaxLines; i++)
        {
            var line = request.Lines[i];
            if (line is null)
            {
                errors[$"lines[{i}]"] = ["a line is required"];
                continue;
            }

            if (!SkuPattern().IsMatch(line.Sku))
            {
                errors[$"lines[{i}].sku"] = [$"1-{OrderLimits.MaxSkuLength} characters: upper-case letters, digits or '-', starting with a letter or digit"];
            }

            if (line.Quantity is < OrderLimits.MinQuantity or > OrderLimits.MaxQuantity)
            {
                errors[$"lines[{i}].quantity"] = [$"{OrderLimits.MinQuantity}-{OrderLimits.MaxQuantity}"];
            }
        }

        return errors;
    }

    /// <summary>Idempotency keys: 1-100 visible ASCII characters.</summary>
    public static bool IsValidIdempotencyKey(string? key) =>
        !string.IsNullOrEmpty(key) && key.Length <= OrderLimits.MaxIdempotencyKeyLength && key.All(c => c is > ' ' and <= '~');

    /// <summary>
    /// SHA-256 of the normalized request: a versioned, fixed rendering of the validated fields in request
    /// order (line order is meaningful: it defines the line numbers). Property order and whitespace
    /// of the original body do not affect it; member names are camelCase only (strict reading).
    /// </summary>
    public static byte[] Hash(SubmitOrderRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var normalized = new
        {
            v = 1,
            customerId = request.CustomerId,
            lines = request.Lines.Select(l => new { sku = l.Sku, quantity = l.Quantity }).ToArray(),
        };
        return SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(normalized, HashOptions)));
    }

    [GeneratedRegex("^[A-Za-z0-9._-]{1,64}$")]
    private static partial Regex CustomerIdPattern();

    [GeneratedRegex("^[A-Z0-9][A-Z0-9-]{0,31}$")]
    private static partial Regex SkuPattern();
}
