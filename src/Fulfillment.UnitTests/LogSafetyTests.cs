using System.Text.RegularExpressions;
using Xunit;

namespace Fulfillment.UnitTests;

/// <summary>
/// Log safety: no log message template in the solution has a placeholder that would carry a payload, a secret, or
/// personal data. Logs carry ids, codes, states, and counts only (payloads stay in the databases). The check reads
/// every [LoggerMessage] template in the source, so it also covers services this test project does not reference.
/// </summary>
public sealed partial class LogSafetyTests
{
    private static readonly HashSet<string> Forbidden = new(StringComparer.OrdinalIgnoreCase)
    {
        "Payload", "Body", "Data", "Content", "Envelope", "Request", "Response", "Evidence", "Fields",
        "Password", "Secret", "Token", "ApiKey", "Signature", "ConnectionString",
        "Customer", "CustomerId", "Email", "Address", "Name", "Phone",
    };

    // Names that end like a forbidden one but carry no payload or personal data.
    private static readonly HashSet<string> Allowed = new(StringComparer.Ordinal) { "EventName", "Name" };

    [GeneratedRegex("""\[LoggerMessage\([^\]]*?Message\s*=\s*"(?<template>(?:[^"\\]|\\.)*)"[^\]]*\)\]""", RegexOptions.Singleline)]
    private static partial Regex LoggerMessage();

    [GeneratedRegex(@"\{(?<name>[A-Za-z]+)\}")]
    private static partial Regex Placeholder();

    private static string SourceRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Fulfillment.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("solution root (Fulfillment.slnx) not found above the test output");
    }

    [Fact]
    public void LogTemplates_HaveNoPayloadSecretOrPersonalDataPlaceholders()
    {
        var templates = Directory.EnumerateFiles(SourceRoot(), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .SelectMany(f => LoggerMessage().Matches(File.ReadAllText(f)).Select(m => (File: Path.GetFileName(f), Template: m.Groups["template"].Value)))
            .ToList();

        var violations = templates
            .SelectMany(t => Placeholder().Matches(t.Template).Select(p => p.Groups["name"].Value)
                .Where(n => !Allowed.Contains(n) && Forbidden.Any(f => n.Equals(f, StringComparison.OrdinalIgnoreCase) || n.EndsWith(f, StringComparison.Ordinal) && f.Length > 4))
                .Select(n => $"{t.File}: {{{n}}} in \"{t.Template}\""))
            .ToList();

        Assert.True(templates.Count > 50, $"expected the solution's log templates, found {templates.Count}");
        Assert.Empty(violations);
    }
}
