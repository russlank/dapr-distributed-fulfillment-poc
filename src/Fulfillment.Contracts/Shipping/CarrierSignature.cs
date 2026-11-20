using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Fulfillment.Contracts.Shipping;

/// <summary>HMAC-SHA256 signature of a carrier callback: hex of HMAC(key, "{unix seconds}.{body}").</summary>
public static class CarrierSignature
{
    /// <summary>Callbacks older or newer than this (by the receiver's clock) are refused.</summary>
    public static readonly TimeSpan MaxSkew = TimeSpan.FromMinutes(5);

    public static string Sign(string key, long timestamp, string body)
    {
        ArgumentNullException.ThrowIfNull(key);
        var mac = HMACSHA256.HashData(Encoding.UTF8.GetBytes(key), Encoding.UTF8.GetBytes(timestamp.ToString(CultureInfo.InvariantCulture) + "." + body));
        return Convert.ToHexStringLower(mac);
    }

    public static bool Verify(string key, string? timestampHeader, string? signatureHeader, string body, DateTimeOffset now)
    {
        if (string.IsNullOrEmpty(key) || signatureHeader is null
            || !long.TryParse(timestampHeader, NumberStyles.None, CultureInfo.InvariantCulture, out var timestamp)
            || Math.Abs(now.ToUnixTimeSeconds() - timestamp) > MaxSkew.TotalSeconds)
        {
            return false;
        }

        var expected = Encoding.ASCII.GetBytes(Sign(key, timestamp, body));
        var provided = Encoding.ASCII.GetBytes(signatureHeader.ToLowerInvariant());
        return CryptographicOperations.FixedTimeEquals(expected, provided);
    }
}

/// <summary>The canonical payload hash of a carrier status event (the order API's ingress and Shipping compute the same).</summary>
public static class CarrierEventHash
{
    public static string Of(Guid eventId, Guid shipmentId, Guid orderId, string status, int version, DateTime occurredAtUtc) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('|',
            eventId.ToString("N"), shipmentId.ToString("N"), orderId.ToString("N"), status, version.ToString(CultureInfo.InvariantCulture),
            occurredAtUtc.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture)))));
}
