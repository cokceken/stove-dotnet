using System.Globalization;
using System.Text;
using Confluent.Kafka;
using Confluent.Kafka.Admin;
using Testcontainers.Kafka;

namespace StoveDotnet.Kafka;

/// <summary>
/// A real Kafka broker for the application under test. Verification is black-box: Stove observes every topic through
/// its own consumer and reads consumer-group offsets, so the application needs no Stove-specific code.
/// </summary>
public sealed class KafkaSystem : ExposingSystem<KafkaOptions, KafkaExposedConfiguration>, IRunAware, IFailureDetailsProvider
{
    private readonly MessageStore _store = new();
    private readonly CancellationTokenSource _stopping = new();
    private readonly string _observerGroupId = $"stove-observer-{Guid.NewGuid():N}";
    private KafkaContainer? _container;
    private IProducer<byte[]?, byte[]?>? _producer;
    private IAdminClient? _admin;
    private Task? _observer;
    private int _disposed;

    public KafkaSystem(string? name, KafkaOptions options)
        : base(name, options)
    {
    }

    /// <summary>Admin client for anything the DSL does not cover (topics, groups, configs).</summary>
    public IAdminClient Admin => _admin ?? throw new InvalidOperationException($"{DisplayName} is not running yet.");

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        string bootstrapServers;
        var runMigrations = true;
        if (Options.ExistingBootstrapServers is { } existing)
        {
            bootstrapServers = existing;
            runMigrations = Options.RunMigrationsOnExisting;
        }
        else
        {
            var builder = new KafkaBuilder(Options.Image);
            _container = (Options.ConfigureContainer?.Invoke(builder) ?? builder).Build();
            await _container.StartAsync(cancellationToken).ConfigureAwait(false);
            bootstrapServers = _container.GetBootstrapAddress();
        }

        var exposed = new KafkaExposedConfiguration(bootstrapServers);
        Expose(exposed);

        _admin = new AdminClientBuilder(ClientConfig(new AdminClientConfig())).Build();
        _producer = new ProducerBuilder<byte[]?, byte[]?>(ClientConfig(new ProducerConfig { Acks = Acks.All, LingerMs = 0 })).Build();

        if (runMigrations)
        {
            await Options.Migrations.RunAsync(new KafkaMigrationContext(_admin, exposed), cancellationToken).ConfigureAwait(false);
        }

        _observer = StartObserver();
    }

    /// <summary>Publishes <paramref name="message"/> as the running test, serialized with the configured serde.</summary>
    public Task<TopicPartitionOffset> Publish<T>(string topic, T message, string? key = null, IDictionary<string, string>? headers = null)
    {
        ArgumentNullException.ThrowIfNull(message);
        return PublishRaw(topic, Options.Serde.Serialize(message, typeof(T)), key is null ? null : Encoding.UTF8.GetBytes(key), headers);
    }

    /// <summary>Publishes raw bytes as the running test.</summary>
    public async Task<TopicPartitionOffset> PublishRaw(string topic, byte[]? value, byte[]? key = null, IDictionary<string, string>? headers = null)
    {
        var test = StoveTestContext.Require();
        var kafkaHeaders = new Headers();
        foreach (var (name, headerValue) in test.CorrelationHeaders().Concat(headers ?? new Dictionary<string, string>()))
        {
            kafkaHeaders.Remove(name);
            kafkaHeaders.Add(name, Encoding.UTF8.GetBytes(headerValue));
        }

        using var activity = test.StartCorrelatedActivity("stove.kafka.publish");
        var result = await Producer.ProduceAsync(
            topic,
            new Message<byte[]?, byte[]?> { Key = key, Value = value, Headers = kafkaHeaders },
            test.CancellationToken).ConfigureAwait(false);
        return result.TopicPartitionOffset;
    }

    /// <summary>Waits until a message of the running test matching <paramref name="condition"/> appears on the broker.</summary>
    public Task<ObservedMessage<T>> ShouldBePublished<T>(Func<ObservedMessage<T>, bool> condition, TimeSpan? timeout = null, string? topic = null) =>
        WaitForMessage(condition, timeout, record => topic is null || record.Topic == topic, "published");

    /// <summary>
    /// Observes for the entire specified interval and fails if a matching message is seen. This is a bounded
    /// observation guarantee, not proof that no message can arrive after the interval or while the observer is delayed.
    /// </summary>
    public async Task ShouldNotBePublished<T>(Func<ObservedMessage<T>, bool> condition, TimeSpan within, string? topic = null)
    {
        ArgumentNullException.ThrowIfNull(condition);
        if (within <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(within), "An observation interval must be positive.");
        }

        var test = StoveTestContext.Require();
        var started = TimeProvider.System.GetTimestamp();
        while (true)
        {
            test.CancellationToken.ThrowIfCancellationRequested();
            var changed = _store.Changed.NextChange();
            if (Matching(test, record => topic is null || record.Topic == topic, condition).FirstOrDefault() is { } message)
            {
                throw new StoveAssertionException($"Unexpected {typeof(T).Name} observed at {message.Topic}[{message.Record.Partition}]@{message.Record.Offset}.");
            }

            var remaining = within - TimeProvider.System.GetElapsedTime(started);
            if (remaining <= TimeSpan.Zero) return;
            try
            {
                await changed.WaitAsync(remaining, test.CancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // Inspect once more at the end of the observation interval.
            }
        }
    }

    /// <summary>
    /// Waits until a matching message exists and a consumer group (other than Stove's) has committed past it. Consumer
    /// groups come from <see cref="KafkaOptions.ConsumerGroups"/> unless <paramref name="consumerGroup"/> is given.
    /// </summary>
    public async Task<ObservedMessage<T>> ShouldBeConsumed<T>(
        Func<ObservedMessage<T>, bool> condition,
        TimeSpan? timeout = null,
        string? topic = null,
        string? consumerGroup = null)
    {
        var test = StoveTestContext.Require();
        var wait = timeout ?? Options.DefaultTimeout;
        var started = TimeProvider.System.GetTimestamp();
        var message = await ShouldBePublished(condition, wait, topic).ConfigureAwait(false);
        var record = message.Record;

        var lastSeen = new Dictionary<string, long>(StringComparer.Ordinal);
        await Eventually.UntilAsync(
            async ct =>
            {
                foreach (var group in await ConsumerGroups(consumerGroup, ct).ConfigureAwait(false))
                {
                    var committed = await CommittedOffset(group, record.Topic, record.Partition).ConfigureAwait(false);
                    lastSeen[group] = committed;
                    if (committed > record.Offset)
                    {
                        return true;
                    }
                }

                return false;
            },
            (wait - TimeProvider.System.GetElapsedTime(started)) is var remaining && remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero,
            () => $"{typeof(T).Name} at {record.Topic}[{record.Partition}]@{record.Offset} was published but not consumed. " +
                  $"Committed offsets: {(lastSeen.Count == 0 ? "(no consumer groups found)" : string.Join(", ", lastSeen.Select(g => $"{g.Key}={g.Value}")))}",
            cancellationToken: test.CancellationToken).ConfigureAwait(false);

        return message;
    }

    /// <summary>Waits until a matching message of the running test appears on an error topic (see <see cref="KafkaOptions.ErrorTopicSuffixes"/>).</summary>
    public Task<ObservedMessage<T>> ShouldBeFailed<T>(Func<ObservedMessage<T>, bool> condition, TimeSpan? timeout = null) =>
        WaitForMessage(condition, timeout, record => Options.ErrorTopicSuffixes.Any(s => record.Topic.EndsWith(s, StringComparison.Ordinal)), "published to an error topic");

    /// <summary>Messages of the running test observed so far that deserialize to <typeparamref name="T"/>.</summary>
    public IReadOnlyList<ObservedMessage<T>> Peek<T>(string? topic = null)
    {
        var test = StoveTestContext.Require();
        return Matching<T>(test, record => topic is null || record.Topic == topic, _ => true).ToList();
    }

    public Task<FailureDetails?> DescribeAsync(StoveTestContext test, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(test);
        var records = Owned(test).ToList();
        var content = new StringBuilder().AppendLine(CultureInfo.InvariantCulture, $"Messages observed ({records.Count}):");
        foreach (var record in records.TakeLast(50))
        {
            content.AppendLine(CultureInfo.InvariantCulture,
                $"  {record.Topic}[{record.Partition}]@{record.Offset} key={record.KeyAsString ?? "(null)"} value={Truncate(record.ValueAsString)}");
        }

        return Task.FromResult<FailureDetails?>(new FailureDetails(Name is null ? "kafka" : $"kafka '{Name}'", content.ToString().TrimEnd()));
    }

    private async Task<ObservedMessage<T>> WaitForMessage<T>(
        Func<ObservedMessage<T>, bool> condition,
        TimeSpan? timeout,
        Func<ObservedRecord, bool> recordFilter,
        string expectation)
    {
        ArgumentNullException.ThrowIfNull(condition);
        var test = StoveTestContext.Require();
        ObservedMessage<T>? match = null;
        await Eventually.UntilAsync(
            _ => ValueTask.FromResult((match = Matching(test, recordFilter, condition).FirstOrDefault()) is not null),
            timeout ?? Options.DefaultTimeout,
            () => $"no {typeof(T).Name} matching the condition was {expectation}. {DescribeObserved(test)}",
            _store.Changed,
            test.CancellationToken).ConfigureAwait(false);
        return match!;
    }

    private IEnumerable<ObservedMessage<T>> Matching<T>(StoveTestContext test, Func<ObservedRecord, bool> recordFilter, Func<ObservedMessage<T>, bool> condition)
    {
        foreach (var record in Owned(test).Where(recordFilter))
        {
            if (record.Value is null || !TryDeserialize<T>(record.Value, out var value))
            {
                continue;
            }

            var message = new ObservedMessage<T>(value, record);
            if (condition(message))
            {
                yield return message;
            }
        }
    }

    private IEnumerable<ObservedRecord> Owned(StoveTestContext test) =>
        _store.Snapshot().Where(r => test.Owns(r.Header(StoveHeaders.TestId), r.Header(StoveHeaders.Traceparent)));

    private bool TryDeserialize<T>(byte[] data, out T value)
    {
        try
        {
            if (Options.Serde.Deserialize(data, typeof(T)) is T typed)
            {
                value = typed;
                return true;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Not a T; the record simply does not match.
        }

        value = default!;
        return false;
    }

    private string DescribeObserved(StoveTestContext test)
    {
        var records = Owned(test).ToList();
        return records.Count == 0
            ? "No messages were observed for this test."
            : "Observed: " + string.Join("; ", records.TakeLast(10).Select(r => $"{r.Topic}@{r.Offset} {Truncate(r.ValueAsString)}"));
    }

    private async Task<IReadOnlyList<string>> ConsumerGroups(string? requested, CancellationToken cancellationToken)
    {
        if (requested is not null)
        {
            return [requested];
        }

        if (Options.ConsumerGroups.Count > 0)
        {
            return Options.ConsumerGroups.ToList();
        }

        cancellationToken.ThrowIfCancellationRequested();
        var groups = await Admin.ListConsumerGroupsAsync().ConfigureAwait(false);
        return groups.Valid.Select(g => g.GroupId).Where(g => !g.StartsWith("stove-observer-", StringComparison.Ordinal)).ToList();
    }

    private async Task<long> CommittedOffset(string group, string topic, int partition)
    {
        var results = await Admin.ListConsumerGroupOffsetsAsync(
            [new ConsumerGroupTopicPartitions(group, [new TopicPartition(topic, partition)])]).ConfigureAwait(false);
        return results.SelectMany(r => r.Partitions)
            .Where(p => p.Topic == topic && p.Partition.Value == partition && p.Offset != Offset.Unset)
            .Select(p => p.Offset.Value)
            .DefaultIfEmpty(-1)
            .Max();
    }

    private Task StartObserver()
    {
        var config = ClientConfig(new ConsumerConfig
        {
            GroupId = _observerGroupId,
            AutoOffsetReset = AutoOffsetReset.Earliest,
            EnableAutoCommit = false,
            TopicMetadataRefreshIntervalMs = 1000,
            AllowAutoCreateTopics = false,
        });

        var consumer = new ConsumerBuilder<byte[]?, byte[]?>(config).SetErrorHandler((_, _) => { }).Build();
        consumer.Subscribe(Options.ObservedTopicsPattern);

        return Task.Factory.StartNew(() =>
        {
            using (consumer)
            {
                while (!_stopping.IsCancellationRequested)
                {
                    try
                    {
                        var result = consumer.Consume(TimeSpan.FromMilliseconds(100));
                        if (result?.Message is { } message && !result.IsPartitionEOF)
                        {
                            _store.Add(new ObservedRecord(
                                result.Topic,
                                result.Partition.Value,
                                result.Offset.Value,
                                message.Key,
                                message.Value,
                                (message.Headers ?? [])
                                    .GroupBy(h => h.Key, StringComparer.OrdinalIgnoreCase)
                                    .ToDictionary(g => g.Key, g => Encoding.UTF8.GetString(g.Last().GetValueBytes()), StringComparer.OrdinalIgnoreCase),
                                message.Timestamp.UtcDateTime));
                        }
                    }
                    catch (ConsumeException)
                    {
                        // Transient broker errors (e.g. topic being created); keep observing.
                    }
                }

                consumer.Close();
            }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    private IProducer<byte[]?, byte[]?> Producer => _producer ?? throw new InvalidOperationException($"{DisplayName} is not running yet.");

    private TConfig ClientConfig<TConfig>(TConfig config) where TConfig : ClientConfig
    {
        config.BootstrapServers = ExposedConfiguration.BootstrapServers;
        Options.ConfigureClient?.Invoke(config);
        return config;
    }

    private static string Truncate(string? value) =>
        value is null ? "(null)" : value.Length <= 200 ? value : string.Concat(value.AsSpan(0, 200), "...");

    public override async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        await DisposeResourcesAsync(
            () => new ValueTask(_stopping.CancelAsync()),
            () => _observer is null ? ValueTask.CompletedTask : new ValueTask(_observer),
            () => _admin is not null && Options.Cleanup is not null
                ? new ValueTask(Options.Cleanup(_admin, CancellationToken.None)) : ValueTask.CompletedTask,
            async () =>
            {
                if (_admin is not null && _container is null)
                {
                    try
                    {
                        await _admin.DeleteGroupsAsync([_observerGroupId]).ConfigureAwait(false);
                    }
                    catch (DeleteGroupsException)
                    {
                        // The observer never committed, so the group may not exist.
                    }
                }
            },
            () => { _producer?.Dispose(); return ValueTask.CompletedTask; },
            () => { _admin?.Dispose(); return ValueTask.CompletedTask; },
            () => { _stopping.Dispose(); return ValueTask.CompletedTask; },
            () => _container?.DisposeAsync() ?? ValueTask.CompletedTask).ConfigureAwait(false);
    }
}

public static class KafkaStoveExtensions
{
    public static StoveBuilder WithKafka(this StoveBuilder builder, Action<KafkaOptions>? configure = null) =>
        builder.WithKafka(name: null, configure);

    public static StoveBuilder WithKafka(this StoveBuilder builder, string? name, Action<KafkaOptions>? configure = null)
    {
        var options = new KafkaOptions();
        configure?.Invoke(options);
        return builder.WithSystem(new KafkaSystem(name, options));
    }

    public static KafkaSystem Kafka(this StoveTestContext test, string? name = null) => test.GetSystem<KafkaSystem>(name);
}
