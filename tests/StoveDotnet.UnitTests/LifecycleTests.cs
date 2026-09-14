using Xunit;

namespace StoveDotnet.UnitTests;

public sealed class LifecycleTests
{
    [Fact]
    public async Task Rollback_keeps_startup_error_and_attempts_every_disposal()
    {
        var startup = new InvalidOperationException("migration failed");
        var cleanup = new InvalidOperationException("cleanup failed");
        var stopped = new List<string>();
        var builder = StoveBuilder.Create()
            .WithSystem(new LifecycleSystem("first", _ => Task.CompletedTask, () => { stopped.Add("first"); return ValueTask.CompletedTask; }))
            .WithSystem(new LifecycleSystem("failing", _ => throw startup, () => { stopped.Add("failing"); throw cleanup; }));

        var error = await Assert.ThrowsAsync<AggregateException>(() => builder.StartAsync(TestContext.Current.CancellationToken));

        Assert.Same(startup, error.InnerExceptions[0]);
        Assert.Contains(cleanup, error.Flatten().InnerExceptions);
        Assert.Equal(["failing", "first"], stopped);
    }

    [Fact]
    public async Task Rollback_waits_for_in_flight_startup_even_when_another_start_throws_synchronously()
    {
        var starting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishStart = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopped = false;
        var startup = new InvalidOperationException("start failed");
        var builder = StoveBuilder.Create()
            .WithSystem(new LifecycleSystem("slow", async ct =>
            {
                starting.SetResult();
                await finishStart.Task.WaitAsync(ct);
            }, () => { stopped = true; return ValueTask.CompletedTask; }))
            .WithSystem(new LifecycleSystem("failing", _ => throw startup, () => ValueTask.CompletedTask));

        var run = builder.StartAsync(TestContext.Current.CancellationToken);
        await starting.Task.WaitAsync(TestContext.Current.CancellationToken);
        var stoppedBeforeCompletion = stopped;
        finishStart.SetResult();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => run);

        Assert.False(stoppedBeforeCompletion);
        Assert.True(stopped);
        Assert.Same(startup, error);
    }

    [Fact]
    public async Task Shutdown_stops_app_first_then_all_systems_in_reverse_order_once()
    {
        var stopped = new List<string>();
        var failure = new InvalidOperationException("app stop failed");
        var stove = await StoveBuilder.Create()
            .WithSystem(new LifecycleSystem("first", _ => Task.CompletedTask, () => { stopped.Add("first"); return ValueTask.CompletedTask; }))
            .WithSystem(new LifecycleSystem("last", _ => Task.CompletedTask, () => { stopped.Add("last"); throw new InvalidOperationException("system stop failed"); }))
            .WithApplication(new TestApplication(() => { stopped.Add("app"); throw failure; }))
            .StartAsync(TestContext.Current.CancellationToken);

        var error = await Assert.ThrowsAsync<AggregateException>(() => stove.DisposeAsync().AsTask());
        await stove.DisposeAsync();

        Assert.Equal(["app", "last", "first"], stopped);
        Assert.Same(failure, error.InnerExceptions[0]);
        Assert.Equal(2, error.InnerExceptions.Count);
    }

    [Fact]
    public async Task Cancellation_during_startup_is_preserved_and_resources_are_disposed()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var stopped = false;
        var builder = StoveBuilder.Create().WithSystem(new LifecycleSystem(null, ct =>
        {
            cancellation.Cancel();
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }, () => { stopped = true; return ValueTask.CompletedTask; }));

        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => builder.StartAsync(cancellation.Token));

        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.True(stopped);
    }

    [Fact]
    public async Task Resource_cleanup_continues_after_synchronous_and_asynchronous_failures()
    {
        var first = new InvalidOperationException("callback failed");
        var second = new InvalidOperationException("client disposal failed");
        var released = false;
        var error = await Assert.ThrowsAsync<AggregateException>(() => SystemDisposal.RunAsync([
            () => throw first,
            () => ValueTask.FromException(second),
            () => { released = true; return ValueTask.CompletedTask; },
        ]).AsTask());

        Assert.True(released);
        Assert.Equal([first, second], error.InnerExceptions);
    }

    private sealed class LifecycleSystem(string? name, Func<CancellationToken, Task> start, Func<ValueTask> stop) : IPluggedSystem, IRunAware
    {
        public string? Name => name;
        public Task RunAsync(CancellationToken cancellationToken) => start(cancellationToken);
        public ValueTask DisposeAsync() => stop();
    }

    private sealed class TestApplication(Func<ValueTask> stop) : IApplicationUnderTest, IApplicationContext, IServiceProvider
    {
        public IServiceProvider Services => this;
        public Uri BaseAddress { get; } = new("http://localhost");
        public object? GetService(Type serviceType) => null;
        public Task<IApplicationContext> StartAsync(IReadOnlyDictionary<string, string?> configuration, CancellationToken cancellationToken) =>
            Task.FromResult<IApplicationContext>(this);
        public ValueTask DisposeAsync() => stop();
    }
}
