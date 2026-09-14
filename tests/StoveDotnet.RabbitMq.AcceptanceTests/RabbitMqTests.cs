using System.Net;
using RabbitMQ.Client;
using RabbitMQ.Client.Exceptions;
using StoveDotnet.AspNetCore;
using StoveDotnet.Http;
using Xunit;

namespace StoveDotnet.RabbitMq.AcceptanceTests;

public sealed record TestWork(string Id, string Value);
public sealed record Startup(bool Ready);

public sealed class RabbitFixture : IAsyncLifetime
{
    public Stove Stove { get; private set; } = null!;
    public RabbitMqSystem Primary { get; private set; } = null!;
    public RabbitMqSystem Archive { get; private set; } = null!;
    public static void Configure(RabbitMqOptions options, string name)
    {
        options.ConfigureExposedConfiguration = c => [new($"Rabbit:{name}", c.ConnectionString)];
        options.ConfigureClient = c => c.ClientProvidedName = "stove-contract";
        options.Setup.Add(async (ctx, ct) =>
        {
            await ctx.Channel.QueueDeclareAsync("orders.work", false, false, false, cancellationToken: ct);
            await ctx.Channel.QueueBindAsync("orders.work", "orders", "orders.submit", cancellationToken: ct);
        }, order: 2);
        options.Setup.Add((ctx, ct) => ctx.Channel.ExchangeDeclareAsync("orders", ExchangeType.Topic, cancellationToken: ct), order: 1);
        options.Bindings.Add(new("orders", "orders.#"));
    }
    public async ValueTask InitializeAsync()
    {
        Stove = await StoveBuilder.Create().WithRabbitMq("Primary", o => Configure(o, "Primary"))
            .WithRabbitMq("Archive", o => Configure(o, "Archive"))
            .WithHttpClient().WithAspNetCoreApplication<Program>().StartAsync(TestContext.Current.CancellationToken);
        await Stove.Test(t => { Primary = t.RabbitMq("Primary"); Archive = t.RabbitMq("Archive"); return Task.CompletedTask; });
    }
    public async ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        if (Stove is not null) await Stove.DisposeAsync();
    }
}

public sealed class RabbitMqTests(RabbitFixture fixture) : IClassFixture<RabbitFixture>
{
    [Fact]
    public Task Application_publication_is_observed_with_propagated_headers() => fixture.Stove.Test(async t =>
    {
        var id = Guid.NewGuid().ToString();
        Assert.Equal(HttpStatusCode.Accepted, (await t.Http().Post("/publish", new TestWork(id, "hello"))).StatusCode);
        var message = await t.RabbitMq("Primary").ShouldBePublished<TestWork>(m => m.Value.Id == id);
        Assert.Equal("orders.created", message.Record.RoutingKey);
        Assert.Equal(t.TestId, message.Record.Headers[StoveHeaders.TestId]);
        Assert.Equal("stove-contract", t.RabbitMq("Primary").Connection.ClientProvidedName);
    });

    [Fact]
    public Task Setup_finishes_before_the_real_application_starts() => fixture.Stove.Test(async t =>
        Assert.True((await t.Http().Get<Startup>("/startup")).Body.Ready));

    [Fact]
    public Task Observer_does_not_compete_with_application_processing() => fixture.Stove.Test(async t =>
    {
        var id = Guid.NewGuid().ToString();
        await t.RabbitMq("Primary").Publish("orders", "orders.submit", new TestWork(id, "hello"));
        await t.RabbitMq("Primary").ShouldBePublished<TestWork>(m => m.Value.Id == id, routingKey: "orders.submit");
        await t.RabbitMq("Primary").ShouldBePublished<TestWork>(m => m.Value.Id == id && m.Value.Value == "HELLO", routingKey: "orders.processed");
        Assert.Equal("HELLO", (await t.Http().Get<TestWork>($"/processed/{id}")).Body.Value);
        await using var channel = await t.RabbitMq("Primary").Connection.CreateChannelAsync(cancellationToken: t.CancellationToken);
        Assert.Equal(1u, (await channel.QueueDeclarePassiveAsync("orders.work", t.CancellationToken)).ConsumerCount);
    });

    [Fact]
    public Task Application_rejection_is_distinct_from_publication() => fixture.Stove.Test(async t =>
    {
        var id = Guid.NewGuid().ToString();
        await t.RabbitMq("Primary").Publish("orders", "orders.submit", new TestWork(id, "fail"));
        await t.RabbitMq("Primary").ShouldBePublished<TestWork>(m => m.Value.Id == id && m.Value.Value == "rejected", routingKey: "orders.failed");
        Assert.Equal(HttpStatusCode.NotFound, (await t.Http().Get($"/processed/{id}")).StatusCode);
    });

    [Fact]
    public Task Confirmed_publication_does_not_establish_application_processing() => fixture.Stove.Test(async t =>
    {
        var id = Guid.NewGuid().ToString();
        // This route reaches only Stove's queue, so mandatory routing and confirms both succeed.
        await t.RabbitMq("Primary").Publish("orders", "orders.observer-only", new TestWork(id, "hello"));
        await t.RabbitMq("Primary").ShouldBePublished<TestWork>(m => m.Value.Id == id);
        Assert.Equal(HttpStatusCode.NotFound, (await t.Http().Get($"/processed/{id}")).StatusCode);
    });

    [Fact]
    public Task Mandatory_unroutable_publish_fails() => fixture.Stove.Test(async t =>
    {
        var error = await Assert.ThrowsAsync<PublishReturnException>(() => t.RabbitMq("Primary").Publish("orders", "unbound", new TestWork("unroutable", "hello")));
        Assert.Contains("NO_ROUTE", error.Message, StringComparison.Ordinal);
    });

    [Fact]
    public Task Named_instances_route_identical_ids_independently() => fixture.Stove.Test(async t =>
    {
        var id = Guid.NewGuid().ToString();
        await t.Http().Post("/publish", new TestWork(id, "primary"));
        await t.Http().Post("/archive/publish", new TestWork(id, "archive"));
        Assert.Equal("primary", (await t.RabbitMq("Primary").ShouldBePublished<TestWork>(m => m.Value.Id == id)).Value.Value);
        Assert.Equal("archive", (await t.RabbitMq("Archive").ShouldBePublished<TestWork>(m => m.Value.Id == id)).Value.Value);
        Assert.Throws<InvalidOperationException>(() => t.RabbitMq());
    });

    [Fact]
    public async Task Overlapping_scopes_observe_only_their_own_messages()
    {
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var count = 0;
        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => fixture.Stove.Test(async t =>
        {
            if (Interlocked.Increment(ref count) == 4) ready.SetResult();
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(10), t.CancellationToken);
            await t.RabbitMq("Primary").Publish("orders", "orders.created", new TestWork(t.TestId, "parallel"));
            await t.RabbitMq("Primary").ShouldBePublished<TestWork>(m => m.Value.Id == t.TestId);
            Assert.All(t.RabbitMq("Primary").Peek<TestWork>(), m => Assert.Equal(t.TestId, m.Value.Id));
        })));
    }

    [Fact]
    public async Task Existing_endpoint_skips_setup_and_removes_only_its_observer_queue()
    {
        string queue;
        RabbitMqSystem system = null!;
        await using (var stove = await Existing(o => o.Setup.Add((_, _) => throw new InvalidOperationException("must not run"))))
        {
            await stove.Test(async t =>
            {
                system = t.RabbitMq();
                await system.Publish("orders", "orders.created", new TestWork("existing", "value"));
                await system.ShouldBePublished<TestWork>(m => m.Value.Id == "existing");
            });
            queue = system.ObservationQueue!;
        }
        Assert.False(system.Connection.IsOpen);
        await using var channel = await fixture.Primary.Connection.CreateChannelAsync(cancellationToken: TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<OperationInterruptedException>(() => channel.QueueDeclarePassiveAsync(queue, TestContext.Current.CancellationToken));
        Assert.True(fixture.Primary.Connection.IsOpen);
    }

    [Fact]
    public async Task Cleanup_failure_still_disposes_and_does_not_repeat_cleanup()
    {
        var calls = 0;
        var stove = await Existing(o => o.Cleanup = (_, _) => { calls++; throw new InvalidOperationException("cleanup failed"); });
        RabbitMqSystem system = null!;
        await stove.Test(t => { system = t.RabbitMq(); return Task.CompletedTask; });
        var error = await Assert.ThrowsAsync<AggregateException>(() => stove.DisposeAsync().AsTask());
        Assert.Contains(error.Flatten().InnerExceptions, e => e.Message == "cleanup failed");
        await system.DisposeAsync();
        Assert.Equal(1, calls);
        Assert.False(system.Connection.IsOpen);
        Assert.True(fixture.Primary.Connection.IsOpen);
    }

    [Fact]
    public async Task Setup_failure_preserves_both_errors_and_removes_managed_container()
    {
        var original = new InvalidOperationException("setup failed");
        string connectionString = "";
        var error = await Assert.ThrowsAsync<AggregateException>(() => StoveBuilder.Create().WithRabbitMq(o =>
        {
            o.Setup.Add((ctx, _) => { connectionString = ctx.Configuration.ConnectionString; throw original; });
            o.Cleanup = (_, _) => throw new InvalidOperationException("rollback failed");
        }).StartAsync(TestContext.Current.CancellationToken));
        Assert.Same(original, error.InnerExceptions[0]);
        Assert.Contains(error.Flatten().InnerExceptions, e => e.Message == "rollback failed");
        await Assert.ThrowsAsync<BrokerUnreachableException>(() => new ConnectionFactory { Uri = new Uri(connectionString), RequestedConnectionTimeout = TimeSpan.FromSeconds(1) }.CreateConnectionAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Missing_endpoint_is_validated_without_setup()
    {
        await Assert.ThrowsAsync<BrokerUnreachableException>(() => StoveBuilder.Create().WithRabbitMq(o =>
            o.UseExisting("amqp://stove:stove@127.0.0.1:1", runSetup: false)).StartAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Absence_waits_and_cancellation_reaches_observation()
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        await fixture.Stove.Test(async t =>
        {
            var started = TimeProvider.System.GetTimestamp();
            await t.RabbitMq("Primary").ShouldNotBePublished<TestWork>(_ => true, TimeSpan.FromMilliseconds(150));
            Assert.True(TimeProvider.System.GetElapsedTime(started) >= TimeSpan.FromMilliseconds(150));
            var observation = t.RabbitMq("Primary").ShouldNotBePublished<TestWork>(_ => true, TimeSpan.FromSeconds(10));
            await cts.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => observation);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => t.RabbitMq("Primary").Publish("orders", "orders.created", new TestWork("cancelled", "test")));
        }, cts.Token);
    }

    [Fact]
    public async Task Retention_overflow_fails_negative_assertions_with_diagnostics()
    {
        await using var stove = await Existing(o => o.Observation.MaxMessagesPerTest = 1);
        var error = await Assert.ThrowsAsync<StoveTestFailedException>(() => stove.Test(async t =>
        {
            await t.RabbitMq().Publish("orders", "orders.created", new TestWork("one", "test"));
            await t.RabbitMq().ShouldBePublished<TestWork>(_ => true);
            var observation = t.RabbitMq().ShouldNotBePublished<TestWork>(m => m.Value.Id == "never", TimeSpan.FromSeconds(10));
            await t.RabbitMq().Publish("orders", "orders.created", new TestWork("two", "test"));
            await observation;
        }));
        Assert.Contains("retention limit", error.InnerException!.Message, StringComparison.Ordinal);
        Assert.Contains(error.Details, d => d.Content.Contains("retention limit", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Lost_observer_connection_cannot_pass_absence_assertions()
    {
        await using var stove = await Existing();
        await stove.Test(async t =>
        {
            await t.RabbitMq().Connection.CloseAsync(t.CancellationToken);
            await Assert.ThrowsAsync<StoveAssertionException>(() => t.RabbitMq().ShouldNotBePublished<TestWork>(_ => true, TimeSpan.FromMilliseconds(100)));
        });
    }

    [Fact]
    public async Task Server_cancelled_observer_cannot_pass_absence_assertions()
    {
        await using var stove = await Existing();
        await stove.Test(async t =>
        {
            await using var channel = await t.RabbitMq().Connection.CreateChannelAsync(cancellationToken: t.CancellationToken);
            var observation = t.RabbitMq().ShouldNotBePublished<TestWork>(_ => true, TimeSpan.FromSeconds(10));
            await channel.QueueDeleteAsync(t.RabbitMq().ObservationQueue!, false, false, cancellationToken: t.CancellationToken);
            await Assert.ThrowsAsync<StoveAssertionException>(() => observation);
        });
    }

    [Fact]
    public Task Arrival_during_absence_window_fails_the_assertion() => fixture.Stove.Test(async t =>
    {
        var id = Guid.NewGuid().ToString();
        var observation = t.RabbitMq("Primary").ShouldNotBePublished<TestWork>(m => m.Value.Id == id, TimeSpan.FromSeconds(10));
        await t.RabbitMq("Primary").Publish("orders", "orders.created", new TestWork(id, "unexpected"));
        await Assert.ThrowsAsync<StoveAssertionException>(() => observation);
    });

    [Fact]
    public async Task Native_trace_headers_match_and_invalid_or_missing_headers_do_not()
    {
        await using var stove = await Existing();
        await stove.Test(async t =>
        {
            await using var channel = await t.RabbitMq().Connection.CreateChannelAsync(new CreateChannelOptions(true, true), t.CancellationToken);
            await NativePublish(channel, "missing", null, t.CancellationToken);
            await NativePublish(channel, "invalid", new Dictionary<string, object?> { [StoveHeaders.TestId] = t.TestId, [StoveHeaders.Traceparent] = "invalid" }, t.CancellationToken);
            await NativePublish(channel, "trace", new Dictionary<string, object?> { [StoveHeaders.Traceparent] = System.Text.Encoding.UTF8.GetBytes(t.Traceparent) }, t.CancellationToken);
            await t.RabbitMq().ShouldBePublished<TestWork>(m => m.Value.Id == "trace");
            Assert.Equal(["trace"], t.RabbitMq().Peek<TestWork>().Select(m => m.Value.Id));
        });
        await stove.Test(t => { Assert.Empty(t.RabbitMq().Peek<TestWork>()); return Task.CompletedTask; });
    }

    [Fact]
    public async Task Existing_endpoints_support_a_real_application_round_trip()
    {
        var builder = StoveBuilder.Create();
        foreach (var (name, system) in new[] { ("Primary", fixture.Primary), ("Archive", fixture.Archive) })
            builder.WithRabbitMq(name, o =>
            {
                RabbitFixture.Configure(o, name);
                o.UseExisting(system.ExposedConfiguration.ConnectionString, runSetup: false);
                o.Setup.Add((_, _) => throw new InvalidOperationException("must not run"));
            });
        await using (var stove = await builder.WithHttpClient().WithAspNetCoreApplication<Program>().StartAsync(TestContext.Current.CancellationToken))
            await stove.Test(async t =>
            {
                var id = Guid.NewGuid().ToString();
                Assert.Equal(HttpStatusCode.Accepted, (await t.Http().Post("/publish", new TestWork(id, "external"))).StatusCode);
                await t.RabbitMq("Primary").ShouldBePublished<TestWork>(m => m.Value.Id == id);
            });
        Assert.True(fixture.Primary.Connection.IsOpen);
    }

    [Fact]
    public async Task Observation_requires_explicit_bindings()
    {
        await using var stove = await Existing(o => o.Bindings.Clear());
        await stove.Test(async t =>
            await Assert.ThrowsAsync<InvalidOperationException>(() => t.RabbitMq().ShouldNotBePublished<TestWork>(_ => true, TimeSpan.FromMilliseconds(50))));
    }

    [Fact]
    public async Task Timeout_preserves_test_identity_and_observed_evidence()
    {
        await using var stove = await Existing();
        var error = await Assert.ThrowsAsync<StoveTestFailedException>(() => stove.Test(async t =>
        {
            await t.RabbitMq().Publish("orders", "orders.created", new TestWork("diagnostic", "value"));
            await t.RabbitMq().ShouldBePublished<TestWork>(_ => true);
            await t.RabbitMq().ShouldBePublished<TestWork>(m => m.Value.Id == "absent", TimeSpan.FromMilliseconds(100));
        }));
        Assert.IsType<StoveTimeoutException>(error.InnerException);
        Assert.False(string.IsNullOrWhiteSpace(error.TraceId));
        Assert.Contains(error.Details, d => d.Content.Contains("orders/orders.created", StringComparison.Ordinal));
    }

    private static ValueTask NativePublish(IChannel channel, string id, IDictionary<string, object?>? headers, CancellationToken ct) =>
        channel.BasicPublishAsync("orders", "orders.created", true, new BasicProperties { Headers = headers },
            System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new { id, value = "native" }), ct);

    private Task<Stove> Existing(Action<RabbitMqOptions>? configure = null) => StoveBuilder.Create().WithRabbitMq(o =>
    {
        o.UseExisting(fixture.Primary.ExposedConfiguration.ConnectionString, runSetup: false);
        o.Bindings.Add(new("orders", "orders.#"));
        configure?.Invoke(o);
    }).StartAsync(TestContext.Current.CancellationToken);
}
