namespace Fulfillment.Primitives;

/// <summary>
/// Stable identity of one business effect: "{subjectId:N}:{step}:{qualifier}".
/// The same logical step always yields the same key, so a retried or replayed step is
/// recognized by its receiver; a new effect (compensation, refund, second attempt) uses a
/// new step or qualifier.
/// </summary>
public readonly record struct OperationKey
{
    private OperationKey(string value) => Value = value;

    public string Value { get; }

    public static OperationKey For(Guid subjectId, string step, string qualifier)
    {
        if (subjectId == Guid.Empty)
        {
            throw new ArgumentException("Subject id must not be empty.", nameof(subjectId));
        }

        return new OperationKey($"{subjectId:N}:{Segment(step, nameof(step))}:{Segment(qualifier, nameof(qualifier))}");
    }

    /// <summary>Key in a named scope other than a GUID subject (for example a diagnostic probe id).</summary>
    public static OperationKey For(string scope, string step, string qualifier) =>
        new($"{Segment(scope, nameof(scope))}:{Segment(step, nameof(step))}:{Segment(qualifier, nameof(qualifier))}");

    public override string ToString() => Value;

    private static string Segment(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Key segment must not be empty.", name);
        }

        if (value.Contains(':', StringComparison.Ordinal))
        {
            throw new ArgumentException("Key segment must not contain ':'.", name);
        }

        return value.Trim();
    }
}
