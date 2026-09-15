using Xunit;

namespace StoveDotnet.UnitTests;

public sealed class ApplicationTests
{
    [Fact]
    public async Task Named_applications_have_private_configuration_and_reverse_shutdown()
    {
        var events = new List<string>();
        var first = new App("first", events);
        var second = new App("second", events);
        var stove = await StoveBuilder.Create()
            .WithApplication("first", first, new ApplicationOptions { Configuration = { ["key"] = "one" } })
            .WithApplication("second", second, new ApplicationOptions
            {
                Configuration = { ["key"] = "two" },
                ReadyAsync = (_, _) => { events.Add("ready"); return Task.CompletedTask; }
            }).StartAsync(TestContext.Current.CancellationToken);
        Assert.Same(first, stove.GetApplication("first"));
        Assert.Same(second, stove.GetApplication("second"));
        Assert.Throws<InvalidOperationException>(() => stove.Application);
        Assert.Throws<InvalidOperationException>(() => stove.GetApplication("missing"));
        Assert.Equal("one", first.Configuration!["key"]);
        Assert.Equal("two", second.Configuration!["key"]);
        await stove.DisposeAsync();
        await stove.DisposeAsync();
        Assert.Equal(["start first", "start second", "ready", "stop second", "stop first"], events);
    }

    [Fact]
    public async Task Default_and_single_named_resolution_are_backward_compatible()
    {
        await using var single = await StoveBuilder.Create().WithApplication("api", new App("api", []))
            .StartAsync(TestContext.Current.CancellationToken);
        Assert.Same(single.GetApplication("api"), single.Application);
        await using var mixed = await StoveBuilder.Create().WithApplication(new App("default", []))
            .WithApplication("worker", new App("worker", [])).StartAsync(TestContext.Current.CancellationToken);
        Assert.NotSame(mixed.GetApplication("worker"), mixed.Application);
    }

    [Fact]
    public async Task Failed_readiness_reports_application_and_rolls_back_every_resource()
    {
        var events = new List<string>();
        var failure = new InvalidOperationException("not ready");
        var builder = StoveBuilder.Create().WithApplication("api", new App("api", events))
            .WithApplication("worker", new App("worker", events), new ApplicationOptions { ReadyAsync = (_, _) => throw failure });
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => builder.StartAsync(TestContext.Current.CancellationToken));
        Assert.Contains("worker", error.Message, StringComparison.Ordinal);
        Assert.Same(failure, error.InnerException);
        Assert.Equal(["start api", "start worker", "stop worker", "stop api"], events);
    }

    [Fact]
    public async Task Readiness_cancellation_is_not_wrapped_and_rolls_back()
    {
        var events = new List<string>();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var builder = StoveBuilder.Create().WithApplication("worker", new App("worker", events), new ApplicationOptions
        {
            ReadyAsync = async (_, ct) => { await cts.CancelAsync(); ct.ThrowIfCancellationRequested(); }
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => builder.StartAsync(cts.Token));
        Assert.Equal(["start worker", "stop worker"], events);
    }

    [Fact]
    public void Duplicate_names_and_reused_instances_are_rejected()
    {
        var app = new App("api", []);
        var builder = StoveBuilder.Create().WithApplication("api", app);
        Assert.Throws<InvalidOperationException>(() => builder.WithApplication("api", new App("other", [])));
        Assert.Throws<InvalidOperationException>(() => builder.WithApplication("other", app));
        Assert.Throws<ArgumentException>(() => builder.WithApplication(" ", new App("other", [])));
    }

    private sealed class App(string name, List<string> events) : IApplicationUnderTest, IApplicationContext, IServiceProvider
    {
        public IReadOnlyDictionary<string, string?>? Configuration { get; private set; }
        public IServiceProvider Services => this;
        public Uri BaseAddress { get; } = new("http://localhost");
        public object? GetService(Type serviceType) => null;
        public Task<IApplicationContext> StartAsync(IReadOnlyDictionary<string, string?> configuration, CancellationToken cancellationToken)
        {
            Configuration = configuration;
            events.Add("start " + name);
            return Task.FromResult<IApplicationContext>(this);
        }
        public ValueTask DisposeAsync() { events.Add("stop " + name); return ValueTask.CompletedTask; }
    }
}
