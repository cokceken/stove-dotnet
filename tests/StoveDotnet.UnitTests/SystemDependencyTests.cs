using Xunit;

namespace StoveDotnet.UnitTests;

public sealed class SystemDependencyTests
{
    [Fact]
    public async Task Dependencies_start_first_and_stop_last_even_when_registered_later()
    {
        var events = new List<string>();
        var broker = new System("broker", events);
        var registry = new System("registry", events) { Dependencies = [broker] };
        var stove = await StoveBuilder.Create().WithSystem(registry).WithSystem(broker).StartAsync(TestContext.Current.CancellationToken);
        await stove.DisposeAsync();
        Assert.Equal(["start broker", "start registry", "stop registry", "stop broker"], events);
    }

    [Fact]
    public async Task Failed_dependency_prevents_dependent_start_and_rolls_back()
    {
        var events = new List<string>();
        var broker = new System("broker", events) { Fail = true };
        var registry = new System("registry", events) { Dependencies = [broker] };
        await Assert.ThrowsAsync<InvalidOperationException>(() => StoveBuilder.Create().WithSystem(registry).WithSystem(broker).StartAsync(TestContext.Current.CancellationToken));
        Assert.Equal(["start broker", "stop registry", "stop broker"], events);
    }

    [Fact]
    public async Task Cycles_and_missing_dependencies_fail_before_any_start()
    {
        var events = new List<string>();
        var first = new System("first", events);
        var second = new System("second", events) { Dependencies = [first] };
        first.Dependencies = [second];
        await Assert.ThrowsAsync<InvalidOperationException>(() => StoveBuilder.Create().WithSystem(first).WithSystem(second).StartAsync(TestContext.Current.CancellationToken));
        Assert.DoesNotContain(events, e => e.StartsWith("start", StringComparison.Ordinal));
        events.Clear();
        await Assert.ThrowsAsync<InvalidOperationException>(() => StoveBuilder.Create().WithSystem(second).StartAsync(TestContext.Current.CancellationToken));
        Assert.DoesNotContain(events, e => e.StartsWith("start", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Independent_systems_can_start_while_dependency_is_waiting()
    {
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var events = new List<string>();
        var first = new System("first", events) { Wait = ready.Task };
        var dependent = new System("dependent", events) { Dependencies = [first] };
        var independent = new System("independent", events) { Signal = ready };
        await using var stove = await StoveBuilder.Create().WithSystem(dependent).WithSystem(first).WithSystem(independent)
            .StartAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.True(events.IndexOf("start independent") < events.IndexOf("start dependent"));
    }

    private sealed class System(string name, List<string> events) : IPluggedSystem, IRunAware, ISystemDependencies
    {
        public string? Name => name;
        public IReadOnlyCollection<IPluggedSystem> Dependencies { get; set; } = [];
        public bool Fail { get; init; }
        public Task? Wait { get; init; }
        public TaskCompletionSource? Signal { get; init; }
        public async Task RunAsync(CancellationToken cancellationToken)
        {
            events.Add("start " + name);
            Signal?.TrySetResult();
            if (Fail) throw new InvalidOperationException("startup failed");
            if (Wait is not null) await Wait.WaitAsync(cancellationToken);
        }
        public ValueTask DisposeAsync() { events.Add("stop " + name); return ValueTask.CompletedTask; }
    }
}
