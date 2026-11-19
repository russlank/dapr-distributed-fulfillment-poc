using Microsoft.Extensions.Logging;
using Xunit;

namespace Fulfillment.IntegrationTests.Infrastructure;

/// <summary>
/// Writes log entries, with exceptions, to the current test's output, so a background failure that
/// a component only logs (and then recovers from) is visible when the test fails.
/// </summary>
public sealed class TestOutputLogger<T> : ILogger<T>
{
    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        ArgumentNullException.ThrowIfNull(formatter);
        var output = TestContext.Current.TestOutputHelper;
        if (output is null)
        {
            return;
        }

        try
        {
            output.WriteLine($"{DateTime.UtcNow:HH:mm:ss.fff} {logLevel} {typeof(T).Name}: {formatter(state, exception)}{(exception is null ? "" : Environment.NewLine + exception)}");
        }
        catch (InvalidOperationException)
        {
            // The test already finished.
        }
    }
}
