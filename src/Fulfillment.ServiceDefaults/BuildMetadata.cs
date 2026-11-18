using System;
using System.Linq;
using System.Reflection;

namespace Fulfillment.ServiceDefaults;

/// <summary>
/// Reads values that MSBuild embedded as <see cref="AssemblyMetadataAttribute"/>
/// (Directory.Build.props: <c>&lt;AssemblyMetadata Include="Key" Value="..." /&gt;</c>).
/// Use instead of C# literals for versions owned by the build.
/// </summary>
public static class BuildMetadata
{
    /// <summary>"release" or "drill" (drill images contain failure-injection points).</summary>
    public static string BuildFlavor { get; } = Optional("BuildFlavor") ?? "release";

    /// <summary>Returns the metadata value or throws when the build did not embed it.</summary>
    public static string Required(string key, Assembly? assembly = null)
    {
        string? value = Optional(key, assembly);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException(
                $"Assembly metadata '{key}' is missing from {(assembly ?? typeof(BuildMetadata).Assembly).GetName().Name}. " +
                "Build through the solution so Directory.Build.props can embed it.");
        }

        return value;
    }

    /// <summary>Returns the metadata value, or null when it is absent.</summary>
    public static string? Optional(string key, Assembly? assembly = null) =>
        (assembly ?? typeof(BuildMetadata).Assembly)
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => string.Equals(attribute.Key, key, StringComparison.OrdinalIgnoreCase))?
            .Value;
}
