using System;
using System.Linq;
using System.Reflection;

namespace Fulfillment.ServiceDefaults;

/// <summary>Build identity of one assembly: what to show in logs, About dialogs, and diagnostics endpoints.</summary>
public sealed record ComponentIdentity(
    string AssemblyName,
    string Product,
    string Version,
    string? Commit,
    string? Branch,
    string? RepositoryUrl)
{
    private const int ShortCommitLength = 10;

    /// <summary>"Product v1.2.3-beta (commit abc1234567) [main]".</summary>
    public override string ToString() =>
        $"{Product} v{Version}" +
        (Commit is null ? string.Empty : $" (commit {Commit})") +
        (Branch is null ? string.Empty : $" [{Branch}]");

    public static ComponentIdentity Of<TMarker>() => FromAssembly(typeof(TMarker).Assembly);

    public static ComponentIdentity FromAssembly(Assembly assembly)
    {
        AssemblyName name = assembly.GetName();
        string product = assembly.GetCustomAttribute<AssemblyProductAttribute>()?.Product ?? name.Name ?? "unknown";

        // The SDK appends "+<commit>" to InformationalVersion when SourceLink knows the revision.
        string informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? name.Version?.ToString()
            ?? "unknown";
        string[] parts = informational.Split('+', 2);
        string version = parts[0];

        string? commit = Metadata(assembly, "SourceRevisionId") ?? (parts.Length == 2 ? parts[1] : null);
        if (commit is { Length: > ShortCommitLength })
        {
            commit = commit[..ShortCommitLength];
        }

        return new ComponentIdentity(
            name.Name ?? "unknown",
            product,
            version,
            commit,
            NormalizeBranch(Metadata(assembly, "SourceBranch")),
            Metadata(assembly, "RepositoryUrl"));
    }

    /// <summary>"refs/heads/main" → "main", "refs/tags/v1.0.0" → "v1.0.0"; blank → null.</summary>
    public static string? NormalizeBranch(string? branch)
    {
        if (string.IsNullOrWhiteSpace(branch))
        {
            return null;
        }

        string value = branch.Trim();
        foreach (string prefix in new[] { "refs/heads/", "refs/tags/", "refs/remotes/origin/", "origin/" })
        {
            if (value.StartsWith(prefix, StringComparison.Ordinal))
            {
                return value[prefix.Length..];
            }
        }

        return value;
    }

    private static string? Metadata(Assembly assembly, string key) =>
        assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => string.Equals(attribute.Key, key, StringComparison.OrdinalIgnoreCase))?
            .Value;
}
