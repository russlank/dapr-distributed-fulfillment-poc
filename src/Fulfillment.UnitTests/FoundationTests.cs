using System.Net;
using System.Reflection;
using Fulfillment.Messaging;
using Fulfillment.Primitives;
using Fulfillment.ServiceDefaults;
using Xunit;

namespace Fulfillment.UnitTests;

public class OperationKeyTests
{
    [Fact]
    public void For_SameStep_ReturnsSameKey()
    {
        var order = Guid.Parse("3f2b8c1e-5a4d-4e6f-9a1b-2c3d4e5f6a7b");

        Assert.Equal(OperationKey.For(order, "reserve", "line-3"), OperationKey.For(order, "reserve", "line-3"));
        Assert.Equal("3f2b8c1e5a4d4e6f9a1b2c3d4e5f6a7b:reserve:line-3", OperationKey.For(order, "reserve", "line-3").Value);
    }

    [Fact]
    public void For_DifferentQualifier_ReturnsDifferentKey()
    {
        var order = Guid.NewGuid();

        Assert.NotEqual(OperationKey.For(order, "authorize", "1"), OperationKey.For(order, "authorize", "2"));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("a:b")]
    public void For_InvalidSegment_Throws(string step) =>
        Assert.Throws<ArgumentException>(() => OperationKey.For(Guid.NewGuid(), step, "1"));

    [Fact]
    public void For_EmptyGuid_Throws() =>
        Assert.Throws<ArgumentException>(() => OperationKey.For(Guid.Empty, "reserve", "1"));
}

public class ComponentIdentityTests
{
    [Theory]
    [InlineData("refs/heads/main", "main")]
    [InlineData("refs/tags/v0.1.0", "v0.1.0")]
    [InlineData("origin/main", "main")]
    [InlineData(" main ", "main")]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void NormalizeBranch_StripsRefPrefixes(string? input, string? expected) =>
        Assert.Equal(expected, ComponentIdentity.NormalizeBranch(input));

    [Fact]
    public void CreateIdentity_ReportsVersionFromBuildProps()
    {
        var identity = ServiceDefaultsExtensions.CreateIdentity("test", typeof(ServiceDefaultsExtensions).Assembly, "Development");

        Assert.Equal("test", identity.Component);
        // Development towards 0.5.0 (prerelease suffix dev; build metadata after '+' is allowed).
        Assert.Matches(@"^0\.5\.0-dev(\+.*)?$", identity.Version);
        Assert.Equal(BuildMetadata.BuildFlavor, identity.BuildFlavor);
    }
}

public class FailpointTests
{
    [Fact]
    public void Compiled_MatchesBuildFlavor()
    {
        // Release-flavor builds must contain no failure injection; drill builds must contain it.
        Assert.Equal(Failpoints.Compiled, BuildMetadata.BuildFlavor == "drill");
    }

    [Fact]
    public void ReleaseFlavor_HasNoFailpointImplementation()
    {
        var firing = typeof(Failpoints).GetMethod("LogFired", BindingFlags.NonPublic | BindingFlags.Static);

        if (BuildMetadata.BuildFlavor == "drill")
        {
            Assert.NotNull(firing);
        }
        else
        {
            Assert.Null(firing);
            Assert.Equal("absent", Failpoints.Describe());
        }
    }
}

public class SimulatorBoundaryTests
{
    [Theory]
    [InlineData("Fulfillment.CarrierSimulator")]
    [InlineData("Fulfillment.PaymentProviderSimulator")]
    [InlineData("Fulfillment.ClientSimulator")]
    public void ExternalSimulators_DoNotReferenceDapr(string assemblyName)
    {
        var references = Closure(Assembly.Load(assemblyName));

        Assert.DoesNotContain(references, name => name.StartsWith("Dapr", StringComparison.Ordinal));
    }

    // References of the assembly and of the solution assemblies it uses (ServiceDefaults).
    private static HashSet<string> Closure(Assembly root)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<Assembly>([root]);
        while (queue.Count > 0)
        {
            foreach (var reference in queue.Dequeue().GetReferencedAssemblies())
            {
                if (names.Add(reference.Name!) && reference.Name!.StartsWith("Fulfillment.", StringComparison.Ordinal))
                {
                    queue.Enqueue(Assembly.Load(reference));
                }
            }
        }

        return names;
    }
}

public class SidecarInvokerTests
{
    [Fact]
    public async Task PostAsync_RetriesServerErrors_WithSameIdempotencyKey()
    {
        var handler = new ScriptedHandler(HttpStatusCode.InternalServerError, HttpStatusCode.ServiceUnavailable, HttpStatusCode.OK);
        var invoker = NewInvoker(handler);

        var result = await invoker.PostAsync("shipping-service", "internal/carrier-status", new { id = 1 }, "evt-1", TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Equal(3, result.Attempts);
        Assert.All(handler.IdempotencyKeys, key => Assert.Equal("evt-1", key));
        Assert.Equal("v1.0/invoke/shipping-service/method/internal/carrier-status", handler.Paths[0]);
    }

    [Fact]
    public async Task PostAsync_DoesNotRetryClientErrors()
    {
        var handler = new ScriptedHandler(HttpStatusCode.Conflict, HttpStatusCode.OK);

        var result = await NewInvoker(handler).PostAsync("app", "m", new { }, "k", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Conflict, result.Status);
        Assert.Equal(1, result.Attempts);
    }

    [Fact]
    public async Task PostAsync_StopsAfterMaxAttempts()
    {
        var handler = new ScriptedHandler(Enumerable.Repeat<HttpStatusCode?>(HttpStatusCode.InternalServerError, 10).ToArray());

        var result = await NewInvoker(handler).PostAsync("app", "m", new { }, "k", TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal(InvocationRetryPolicy.Default.MaxAttempts, result.Attempts);
        Assert.Equal(InvocationRetryPolicy.Default.MaxAttempts, handler.Paths.Count);
    }

    [Fact]
    public async Task PostAsync_RetriesTransportFailures()
    {
        var handler = new ScriptedHandler(null, HttpStatusCode.OK);

        var result = await NewInvoker(handler).PostAsync("app", "m", new { }, "k", TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Equal(2, result.Attempts);
    }

    [Fact]
    public async Task PostAsync_AttemptWithoutAnswer_TimesOutAndIsRetriedWithTheSameKey()
    {
        // The first call hangs (a route to a replica that just died); the attempt timeout ends it.
        var calls = 0;
        var keys = new List<string?>();
        using var handler = new DelegateHandler(async (request, ct) =>
        {
            keys.Add(request.Headers.TryGetValues(SidecarInvoker.IdempotencyHeader, out var v) ? v.Single() : null);
            if (Interlocked.Increment(ref calls) == 1)
            {
                await Task.Delay(Timeout.Infinite, ct);
            }

            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
        });
        var policy = InvocationRetryPolicy.Default with { AttemptTimeout = TimeSpan.FromMilliseconds(100) };
        var invoker = new SidecarInvoker(new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:3500/") }, policy, (_, _) => Task.CompletedTask);

        var result = await invoker.PostAsync("workflow-host", "internal/workflows/x/events/e", new { }, "signal:1", TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Equal(2, result.Attempts);
        Assert.Equal(["signal:1", "signal:1"], keys);
    }

    [Fact]
    public async Task PostAsync_CallerCancellation_IsNotRetried()
    {
        using var cts = new CancellationTokenSource();
        using var handler = new DelegateHandler(async (_, ct) =>
        {
            await cts.CancelAsync();
            await Task.Delay(Timeout.Infinite, ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var invoker = new SidecarInvoker(new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:3500/") }, delay: (_, _) => Task.CompletedTask);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => invoker.PostAsync("app", "m", new { }, "k", cts.Token));
    }

    private sealed class DelegateHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }

    [Fact]
    public void DelayBefore_DoublesAndCaps()
    {
        var policy = new InvocationRetryPolicy(6, TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(350));

        Assert.Equal(TimeSpan.FromMilliseconds(100), policy.DelayBefore(2));
        Assert.Equal(TimeSpan.FromMilliseconds(200), policy.DelayBefore(3));
        Assert.Equal(TimeSpan.FromMilliseconds(350), policy.DelayBefore(4));
    }

    private static SidecarInvoker NewInvoker(HttpMessageHandler handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:3500/") }, delay: (_, _) => Task.CompletedTask);

    /// <summary>Returns the scripted statuses in order; null simulates a transport failure.</summary>
    private sealed class ScriptedHandler(params HttpStatusCode?[] statuses) : HttpMessageHandler
    {
        private int next;

        public List<string> Paths { get; } = [];

        public List<string?> IdempotencyKeys { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Paths.Add(request.RequestUri!.PathAndQuery.TrimStart('/'));
            IdempotencyKeys.Add(request.Headers.TryGetValues(SidecarInvoker.IdempotencyHeader, out var v) ? v.Single() : null);
            var status = statuses[Math.Min(next++, statuses.Length - 1)];
            return status is null
                ? throw new HttpRequestException("connection refused")
                : Task.FromResult(new HttpResponseMessage(status.Value) { Content = new StringContent("{}") });
        }
    }
}
