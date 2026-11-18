using Dapr.Actors;
using Dapr.Actors.Runtime;

namespace DaprCompat.App;

public sealed record CounterResult(int Count, string Replica);

public interface ICounterActor : IActor
{
    Task<CounterResult> IncrementAsync();
}

/// <summary>A1/A14: actor state persisted in the SQL Server state store; reports its host replica.</summary>
public sealed class CounterActor(ActorHost host, Evidence evidence) : Actor(host), ICounterActor
{
    public async Task<CounterResult> IncrementAsync()
    {
        var current = await StateManager.TryGetStateAsync<int>("count");
        var next = (current.HasValue ? current.Value : 0) + 1;
        await StateManager.SetStateAsync("count", next);
        await StateManager.SaveStateAsync();
        await evidence.AddAsync("actor", Id.GetId(), $"count={next}");
        return new CounterResult(next, Replica.Name);
    }
}
