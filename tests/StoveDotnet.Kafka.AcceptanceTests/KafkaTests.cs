using Confluent.Kafka;
using Confluent.Kafka.Admin;
using StoveDotnet.Kafka;
using Xunit;

namespace StoveDotnet.Kafka.AcceptanceTests;

public sealed class KafkaFixture : IAsyncLifetime
{
    public Stove Stove { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        Stove = await StoveBuilder.Create()
            .WithKafka(o => o.Migrations.Add((ctx, _) => ctx.Admin.CreateTopicsAsync(
            [
                new TopicSpecification { Name = "orders.created", NumPartitions = 1, ReplicationFactor = 1 },
                new TopicSpecification { Name = "orders.unconsumed", NumPartitions = 1, ReplicationFactor = 1 },
            ])))
            .StartAsync();
    }

    public async ValueTask DisposeAsync() => await Stove.DisposeAsync();
}

public sealed record OrderCreated(string OrderId, decimal Amount);

public sealed class KafkaTests(KafkaFixture fixture) : IClassFixture<KafkaFixture>
{
    private readonly Stove _stove = fixture.Stove;

    [Fact]
    public Task Published_messages_are_observed() => _stove.Test(async t =>
    {
        await t.Kafka().Publish("orders.created", new OrderCreated("o-1", 10m), key: "o-1");

        var message = await t.Kafka().ShouldBePublished<OrderCreated>(m => m.Value.OrderId == "o-1");

        Assert.Equal("orders.created", message.Topic);
        Assert.Equal("o-1", message.Key);
        Assert.Equal(t.TestId, message.Headers[StoveHeaders.TestId]);
    });

    [Fact]
    public Task Consumed_when_a_consumer_group_commits_past_the_message() => _stove.Test(async t =>
    {
        var kafka = t.Kafka();
        await kafka.Publish("orders.created", new OrderCreated("o-2", 20m));

        await ConsumeAndCommit(kafka, "orders-app", "orders.created", t.CancellationToken);

        await kafka.ShouldBeConsumed<OrderCreated>(m => m.Value.OrderId == "o-2", consumerGroup: "orders-app");
    });

    [Fact]
    public Task Not_consumed_messages_time_out_with_details() => _stove.Test(async t =>
    {
        await t.Kafka().Publish("orders.unconsumed", new OrderCreated("o-3", 30m));

        var error = await Assert.ThrowsAsync<StoveTimeoutException>(() =>
            t.Kafka().ShouldBeConsumed<OrderCreated>(m => m.Value.OrderId == "o-3", TimeSpan.FromSeconds(8), consumerGroup: "nobody"));

        Assert.Contains("was published but not consumed", error.Message, StringComparison.Ordinal);
    });

    [Fact]
    public Task Failed_messages_land_on_error_topics() => _stove.Test(async t =>
    {
        await t.Kafka().Publish("payments.error", new OrderCreated("o-4", 40m));

        var failed = await t.Kafka().ShouldBeFailed<OrderCreated>(m => m.Value.OrderId == "o-4", TimeSpan.FromSeconds(20));

        Assert.Equal("payments.error", failed.Topic);
    });

    [Fact]
    public async Task Messages_of_other_tests_are_not_visible()
    {
        await _stove.Test(t => t.Kafka().Publish("orders.created", new OrderCreated("isolated", 1m)));

        await _stove.Test(async t =>
        {
            await t.Kafka().Publish("orders.created", new OrderCreated("mine", 1m));
            await t.Kafka().ShouldBePublished<OrderCreated>(m => m.Value.OrderId == "mine");

            Assert.DoesNotContain(t.Kafka().Peek<OrderCreated>("orders.created"), m => m.Value.OrderId == "isolated");
        });
    }

    [Fact]
    public Task Absence_observation_waits_for_the_full_interval() => _stove.Test(async t =>
    {
        var started = TimeProvider.System.GetTimestamp();
        await t.Kafka().ShouldNotBePublished<OrderCreated>(m => m.Value.OrderId == Guid.Empty.ToString(), TimeSpan.FromMilliseconds(200));
        Assert.True(TimeProvider.System.GetElapsedTime(started) >= TimeSpan.FromMilliseconds(200));
    }, TestContext.Current.CancellationToken);

    [Fact]
    public Task Absence_observation_fails_when_a_message_arrives_during_the_interval() => _stove.Test(async t =>
    {
        var id = Guid.NewGuid().ToString();
        var observation = t.Kafka().ShouldNotBePublished<OrderCreated>(m => m.Value.OrderId == id, TimeSpan.FromSeconds(15), "orders.created");
        await t.Kafka().Publish("orders.created", new OrderCreated(id, 1m));
        var error = await Assert.ThrowsAsync<StoveAssertionException>(() => observation);
        Assert.Contains("orders.created", error.Message, StringComparison.Ordinal);
    }, TestContext.Current.CancellationToken);

    [Fact]
    public async Task Absence_observation_honors_cancellation()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _stove.Test(async t =>
        {
            var observation = t.Kafka().ShouldNotBePublished<OrderCreated>(_ => true, TimeSpan.FromSeconds(30));
            cancellation.Cancel();
            await observation;
        }, cancellation.Token));
    }

    private static async Task ConsumeAndCommit(KafkaSystem kafka, string groupId, string topic, CancellationToken cancellationToken)
    {
        var bootstrapServers = kafka.ExposedConfiguration.BootstrapServers;
        await Task.Run(() =>
        {
            using var consumer = new ConsumerBuilder<Ignore, string>(new ConsumerConfig
            {
                BootstrapServers = bootstrapServers,
                GroupId = groupId,
                AutoOffsetReset = AutoOffsetReset.Earliest,
                EnableAutoCommit = false,
            }).Build();
            consumer.Subscribe(topic);
            var deadline = DateTime.UtcNow.AddSeconds(20);
            while (DateTime.UtcNow < deadline)
            {
                var result = consumer.Consume(TimeSpan.FromMilliseconds(200));
                if (result?.Message?.Value?.Contains("o-2", StringComparison.Ordinal) == true)
                {
                    consumer.Commit(result);
                    break;
                }
            }

            consumer.Close();
        }, cancellationToken);
    }
}
