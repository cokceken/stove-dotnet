using Xunit;

namespace StoveDotnet.UnitTests;

public sealed class CoreTests
{
    [Fact]
    public void Resolves_unnamed_instance_by_default()
    {
        var registry = new SystemRegistry();
        var unnamed = new FakeSystem(null);
        registry.Add(new FakeSystem("a"));
        registry.Add(unnamed);

        Assert.Same(unnamed, registry.Resolve<FakeSystem>(null));
    }

    [Fact]
    public void Resolves_single_named_instance_without_a_name()
    {
        var registry = new SystemRegistry();
        var only = new FakeSystem("payments");
        registry.Add(only);

        Assert.Same(only, registry.Resolve<FakeSystem>(null));
    }

    [Fact]
    public void Requires_a_name_when_several_named_instances_exist()
    {
        var registry = new SystemRegistry();
        registry.Add(new FakeSystem("payments"));
        registry.Add(new FakeSystem("inventory"));

        var error = Assert.Throws<InvalidOperationException>(() => registry.Resolve<FakeSystem>(null));
        Assert.Contains("'payments'", error.Message, StringComparison.Ordinal);
        Assert.Contains("'inventory'", error.Message, StringComparison.Ordinal);
        Assert.Equal("inventory", registry.Resolve<FakeSystem>("inventory").Name);
    }

    [Fact]
    public void Rejects_duplicate_registrations()
    {
        var registry = new SystemRegistry();
        registry.Add(new FakeSystem("x"));

        Assert.Throws<InvalidOperationException>(() => registry.Add(new FakeSystem("x")));
    }

    [Theory]
    [InlineData("00-29326663e1ca49e89820727aa7955f2b-6c1e47ceae684b55-01", "29326663e1ca49e89820727aa7955f2b")]
    [InlineData("00-29326663E1CA49E89820727AA7955F2B-6c1e47ceae684b55-00", "29326663e1ca49e89820727aa7955f2b")]
    [InlineData("garbage", null)]
    [InlineData("00-00000000000000000000000000000000-6c1e47ceae684b55-01", null)]
    [InlineData("00-29326663e1ca49e89820727aa7955f2b-0000000000000000-01", null)]
    [InlineData("00-29326663e1ca49e89820727aa7955f2b", null)]
    [InlineData(null, null)]
    public void Parses_traceparent(string? traceparent, string? expected)
    {
        var parsed = TraceContext.TryParseTraceId(traceparent, out var traceId);

        Assert.Equal(expected is not null, parsed);
        Assert.Equal(expected, traceId);
    }

    [Fact]
    public void Test_name_is_derived_from_file_and_method()
    {
        Assert.Equal("OrderTests.Creates_order", Stove.TestName(@"C:\src\tests\OrderTests.cs", "Creates_order"));
        Assert.Equal("OrderTests.Creates_order", Stove.TestName("/home/ci/tests/OrderTests.cs", "Creates_order"));
    }

    [Fact]
    public async Task Test_context_carries_name_and_correlation()
    {
        await using var stove = await StoveBuilder.Create().StartAsync(TestContext.Current.CancellationToken);

        await stove.Test(t =>
        {
            Assert.Equal("CoreTests.Test_context_carries_name_and_correlation", t.TestName);
            Assert.StartsWith(t.TestName + "#", t.TestId, StringComparison.Ordinal);
            Assert.Matches("^00-[0-9a-f]{32}-[0-9a-f]{16}-01$", t.Traceparent);
            Assert.Same(t, StoveTestContext.Current);

            Assert.True(t.Owns(t.TestId, null));
            Assert.False(t.Owns("other#1", t.Traceparent));
            Assert.True(t.Owns(null, TraceContext.FormatTraceparent(t.TraceId, TraceContext.NewSpanId())));
            Assert.False(t.Owns(null, TraceContext.FormatTraceparent(TraceContext.NewTraceId(), TraceContext.NewSpanId())));
            Assert.True(t.Owns(null, null));
            return Task.CompletedTask;
        });

        Assert.Null(StoveTestContext.Current);
    }

    [Fact]
    public async Task Failures_are_enriched_and_keep_the_original_exception()
    {
        var system = new FakeSystem(null);
        await using var stove = await StoveBuilder.Create().WithSystem(system).StartAsync(TestContext.Current.CancellationToken);
        var original = new InvalidOperationException("expected 201 but was 500");

        var error = await Assert.ThrowsAsync<StoveTestFailedException>(() => stove.Test(_ => throw original));

        Assert.Same(original, error.InnerException);
        Assert.Contains("expected 201 but was 500", error.Message, StringComparison.Ordinal);
        Assert.Contains("--- Stove: fake ---", error.Message, StringComparison.Ordinal);
        Assert.Contains("details for " + error.TestName, error.Message, StringComparison.Ordinal);
        Assert.Equal(1, system.Started);
        Assert.Same(original, system.EndedWith);
    }

    [Fact]
    public async Task Framework_control_flow_exceptions_pass_through()
    {
        await using var stove = await StoveBuilder.Create().StartAsync(TestContext.Current.CancellationToken);

        // A stand-in with NUnit's full type name: Stove matches framework exceptions by name, not by reference.
        await Assert.ThrowsAsync<NUnit.Framework.IgnoreException>(() => stove.Test(_ => throw new NUnit.Framework.IgnoreException()));
        Assert.True(Stove.IsPassThrough(new NUnit.Framework.IgnoreException(), CancellationToken.None));
        Assert.False(Stove.IsPassThrough(new InvalidOperationException(), CancellationToken.None));
    }

    [Fact]
    public async Task Tests_cannot_be_nested()
    {
        await using var stove = await StoveBuilder.Create().StartAsync(TestContext.Current.CancellationToken);

        var error = await Assert.ThrowsAsync<StoveTestFailedException>(() => stove.Test(_ => stove.Test(_ => Task.CompletedTask)));
        Assert.IsType<InvalidOperationException>(error.InnerException);
    }

    [Fact]
    public async Task System_dsl_outside_a_test_is_rejected()
    {
        await Task.Yield();
        Assert.Throws<InvalidOperationException>(() => StoveTestContext.Require());
    }

    [Fact]
    public async Task Eventually_times_out_with_description()
    {
        var error = await Assert.ThrowsAsync<StoveTimeoutException>(() => Eventually.UntilAsync(
            _ => ValueTask.FromResult(false), TimeSpan.FromMilliseconds(200), () => "nothing matched",
            cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("nothing matched", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Eventually_wakes_up_on_signal()
    {
        var signal = new AsyncChangeSignal();
        var ready = false;
        _ = Task.Run(async () =>
        {
            await Task.Delay(100);
            ready = true;
            signal.Notify();
        }, TestContext.Current.CancellationToken);

        await Eventually.UntilAsync(_ => ValueTask.FromResult(ready), TimeSpan.FromSeconds(5), () => "not ready", signal,
            TestContext.Current.CancellationToken);

        Assert.True(ready);
    }

    [Fact]
    public async Task Configuration_keys_must_be_unique_across_systems()
    {
        var builder = StoveBuilder.Create()
            .WithSystem(new FakeSystem("a") { Config = [new("Key", "1")] })
            .WithSystem(new FakeSystem("b") { Config = [new("key", "2")] });

        await Assert.ThrowsAsync<InvalidOperationException>(() => builder.StartAsync(TestContext.Current.CancellationToken));
    }

    private sealed class FakeSystem(string? name) : IPluggedSystem, ITestScopeAware, IFailureDetailsProvider, IExposesConfiguration
    {
        public string? Name { get; } = name;

        public int Started { get; private set; }

        public Exception? EndedWith { get; private set; }

        public List<KeyValuePair<string, string?>> Config { get; init; } = [];

        public Task OnTestStartedAsync(StoveTestContext test)
        {
            Started++;
            return Task.CompletedTask;
        }

        public Task OnTestEndedAsync(StoveTestContext test, Exception? failure)
        {
            EndedWith = failure;
            return Task.CompletedTask;
        }

        public Task<FailureDetails?> DescribeAsync(StoveTestContext test, CancellationToken cancellationToken) =>
            Task.FromResult<FailureDetails?>(new FailureDetails("fake", "details for " + test.TestName));

        public IEnumerable<KeyValuePair<string, string?>> Configuration() => Config;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
