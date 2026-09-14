using System.Text;
using System.Text.Json;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using Testcontainers.RabbitMq;

namespace StoveDotnet.RabbitMq;

public sealed record RabbitMqExposedConfiguration(string ConnectionString) : IExposedConfiguration;
public sealed record RabbitMqSetupContext(IConnection Connection, IChannel Channel, RabbitMqExposedConfiguration Configuration);
/// <summary>A binding from a predeclared exchange to Stove's exclusive observation queue.</summary>
public sealed record RabbitMqBinding(string Exchange, string RoutingKey = "#");

public sealed class RabbitMqOptions : SystemOptions<RabbitMqExposedConfiguration>
{
    public string Image { get; set; } = "rabbitmq:4.1-alpine";
    public string Username { get; set; } = "stove";
    public string Password { get; set; } = "stove";
    public Func<RabbitMqBuilder, RabbitMqBuilder>? ConfigureContainer { get; set; }
    /// <summary>Configures Stove's connection factory. Automatic recovery is disabled: observation gaps fail assertions.</summary>
    public Action<ConnectionFactory>? ConfigureClient { get; set; }
    public JsonSerializerOptions JsonSerializerOptions { get; set; } = new(JsonSerializerDefaults.Web);
    public TimeSpan DefaultTimeout { get; set; } = TimeSpan.FromSeconds(10);
    public MessageObservationOptions Observation { get; } = new();
    public IList<RabbitMqBinding> Bindings { get; } = [];
    /// <summary>Ordered exchange/queue/binding setup. The callback channel is disposed after setup.</summary>
    public MigrationCollection<RabbitMqSetupContext> Setup { get; } = new();
    /// <summary>Runs on shutdown in both modes with a new channel. Does not run if no connection was established.</summary>
    public Func<RabbitMqSetupContext, CancellationToken, Task>? Cleanup { get; set; }
    internal string? ExistingConnectionString { get; private set; }
    internal bool RunSetupOnExisting { get; private set; } = true;

    public RabbitMqOptions UseExisting(string connectionString, bool runSetup = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ExistingConnectionString = connectionString;
        RunSetupOnExisting = runSetup;
        return this;
    }
}

/// <summary>A copied delivery from Stove's queue, not evidence of application processing.</summary>
public sealed record RabbitMqRecord(string Exchange, string RoutingKey, byte[] Body, IReadOnlyDictionary<string, string> Headers, string? MessageId, DateTimeOffset ObservedAt);
public sealed record RabbitMqMessage<T>(T Value, RabbitMqRecord Record);

public sealed class RabbitMqSystem : ExposingSystem<RabbitMqOptions, RabbitMqExposedConfiguration>, IRunAware, ITestScopeAware, IFailureDetailsProvider
{
    private readonly ScopedMessageBuffer<RabbitMqRecord> _store;
    private readonly SemaphoreSlim _publishLock = new(1, 1);
    private RabbitMqContainer? _container;
    private IConnection? _connection;
    private IChannel? _publisher;
    private IChannel? _observer;
    private int _disposed;

    public RabbitMqSystem(string? name, RabbitMqOptions options) : base(name, options) => _store = new(options.Observation);
    /// <summary>Owned by Stove. Native callers create/dispose their own channels and serialize concurrent channel use.</summary>
    public IConnection Connection => _connection ?? throw new InvalidOperationException($"{DisplayName} is not running yet.");
    public string? ObservationQueue { get; private set; }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        foreach (var binding in Options.Bindings) ArgumentException.ThrowIfNullOrWhiteSpace(binding.Exchange);
        var connectionString = Options.ExistingConnectionString;
        if (connectionString is null)
        {
            var builder = new RabbitMqBuilder(Options.Image).WithUsername(Options.Username).WithPassword(Options.Password);
            _container = (Options.ConfigureContainer?.Invoke(builder) ?? builder).Build();
            await _container.StartAsync(cancellationToken).ConfigureAwait(false);
            connectionString = _container.GetConnectionString();
        }
        var factory = new ConnectionFactory { Uri = new Uri(connectionString), ClientProvidedName = "stove-rabbitmq" };
        Options.ConfigureClient?.Invoke(factory);
        factory.AutomaticRecoveryEnabled = false;
        _connection = await factory.CreateConnectionAsync(cancellationToken).ConfigureAwait(false);
        _connection.ConnectionShutdownAsync += (_, args) => { ObserverFailed($"connection closed: {args.ReplyText}"); return Task.CompletedTask; };
        var exposed = new RabbitMqExposedConfiguration(connectionString);
        Expose(exposed);
        if (Options.ExistingConnectionString is null || Options.RunSetupOnExisting)
        {
            await using var channel = await Connection.CreateChannelAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            await Options.Setup.RunAsync(new(Connection, channel, exposed), cancellationToken).ConfigureAwait(false);
        }
        _publisher = await Connection.CreateChannelAsync(new CreateChannelOptions(true, true), cancellationToken).ConfigureAwait(false);
        if (Options.Bindings.Count == 0) return;
        _observer = await Connection.CreateChannelAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        _observer.ChannelShutdownAsync += (_, args) => { ObserverFailed($"observer channel closed: {args.ReplyText}"); return Task.CompletedTask; };
        _observer.CallbackExceptionAsync += (_, args) => { ObserverFailed($"observer callback failed: {args.Exception.Message}"); return Task.CompletedTask; };
        var queue = await _observer.QueueDeclareAsync("", durable: false, exclusive: true, autoDelete: true, cancellationToken: cancellationToken).ConfigureAwait(false);
        ObservationQueue = queue.QueueName;
        foreach (var binding in Options.Bindings)
            await _observer.QueueBindAsync(queue.QueueName, binding.Exchange, binding.RoutingKey, cancellationToken: cancellationToken).ConfigureAwait(false);
        await _observer.BasicQosAsync(0, 32, false, cancellationToken).ConfigureAwait(false);
        var consumer = new AsyncEventingBasicConsumer(_observer);
        consumer.UnregisteredAsync += (_, _) => { ObserverFailed("observer consumer cancelled"); return Task.CompletedTask; };
        consumer.ReceivedAsync += async (_, args) =>
        {
            // RabbitMQ owns the delivery memory; copy everything retained before the callback returns.
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (key, value) in args.BasicProperties.Headers ?? new Dictionary<string, object?>())
                headers[key] = value switch { byte[] bytes => Encoding.UTF8.GetString(bytes), string text => text, _ => "(non-text)" };
            var record = new RabbitMqRecord(args.Exchange, args.RoutingKey, args.Body.ToArray(), headers, args.BasicProperties.MessageId, DateTimeOffset.UtcNow);
            _store.Add(record, (long)record.Body.Length + Encoding.UTF8.GetByteCount(record.Exchange) + Encoding.UTF8.GetByteCount(record.RoutingKey)
                + Encoding.UTF8.GetByteCount(record.MessageId ?? "") + headers.Sum(h => (long)Encoding.UTF8.GetByteCount(h.Key) + Encoding.UTF8.GetByteCount(h.Value)),
                headers.GetValueOrDefault(StoveHeaders.TestId), headers.GetValueOrDefault(StoveHeaders.Traceparent));
            await _observer.BasicAckAsync(args.DeliveryTag, false, args.CancellationToken).ConfigureAwait(false);
        };
        await _observer.BasicConsumeAsync(queue.QueueName, false, consumer, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Publishes JSON, awaiting broker confirms. Mandatory routing defaults on; a route can be satisfied by Stove's queue alone.</summary>
    public Task Publish<T>(string exchange, string routingKey, T message, IDictionary<string, string>? headers = null, bool mandatory = true) =>
        PublishRaw(exchange, routingKey, JsonSerializer.SerializeToUtf8Bytes(message, Options.JsonSerializerOptions), headers, mandatory, "application/json");

    public async Task PublishRaw(string exchange, string routingKey, ReadOnlyMemory<byte> body, IDictionary<string, string>? headers = null, bool mandatory = true, string contentType = "application/octet-stream")
    {
        var test = StoveTestContext.Require();
        using var activity = test.StartCorrelatedActivity("stove.rabbitmq.publish");
        var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in test.CorrelationHeaders().Concat(headers ?? new Dictionary<string, string>())) values[key] = Encoding.UTF8.GetBytes(value);
        var properties = new BasicProperties { Headers = values, ContentType = contentType, MessageId = Guid.NewGuid().ToString("N") };
        await _publishLock.WaitAsync(test.CancellationToken).ConfigureAwait(false);
        try
        {
            await (_publisher ?? throw new InvalidOperationException("RabbitMQ is not running."))
                .BasicPublishAsync(exchange, routingKey, mandatory, properties, body, test.CancellationToken).ConfigureAwait(false);
        }
        finally { _publishLock.Release(); }
    }

    public IReadOnlyList<RabbitMqMessage<T>> Peek<T>(string? exchange = null, string? routingKey = null) => Matching<T>(StoveTestContext.Require(), exchange, routingKey).ToList();

    /// <summary>Waits for a copy routed to Stove's queue. Does not establish application delivery, acknowledgement or processing.</summary>
    public async Task<RabbitMqMessage<T>> ShouldBePublished<T>(Func<RabbitMqMessage<T>, bool> condition, TimeSpan? timeout = null, string? exchange = null, string? routingKey = null)
    {
        ArgumentNullException.ThrowIfNull(condition);
        var test = StoveTestContext.Require();
        RabbitMqMessage<T>? match = null;
        await Eventually.UntilAsync(_ => ValueTask.FromResult((match = Matching<T>(test, exchange, routingKey).FirstOrDefault(condition)) is not null),
            timeout ?? Options.DefaultTimeout, () => $"No {typeof(T).Name} matching the condition reached RabbitMQ observation bindings.", _store.Changed, test.CancellationToken).ConfigureAwait(false);
        return match!;
    }

    public async Task ShouldNotBePublished<T>(Func<RabbitMqMessage<T>, bool> condition, TimeSpan within, string? exchange = null, string? routingKey = null)
    {
        ArgumentNullException.ThrowIfNull(condition);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(within, TimeSpan.Zero);
        var test = StoveTestContext.Require();
        var started = TimeProvider.System.GetTimestamp();
        while (true)
        {
            test.CancellationToken.ThrowIfCancellationRequested();
            var change = _store.Changed.NextChange();
            if (Matching<T>(test, exchange, routingKey).Any(condition)) throw new StoveAssertionException($"Unexpected {typeof(T).Name} reached RabbitMQ observation bindings.");
            var remaining = within - TimeProvider.System.GetElapsedTime(started);
            if (remaining <= TimeSpan.Zero) return;
            try { await change.WaitAsync(remaining, test.CancellationToken).ConfigureAwait(false); }
            catch (TimeoutException) { }
        }
    }

    private IEnumerable<RabbitMqMessage<T>> Matching<T>(StoveTestContext test, string? exchange, string? routingKey)
    {
        test.CancellationToken.ThrowIfCancellationRequested();
        if (ObservationQueue is null) throw new InvalidOperationException("Configure RabbitMqOptions.Bindings before using observation assertions.");
        foreach (var record in _store.Snapshot(test))
        {
            if (exchange is not null && record.Exchange != exchange || routingKey is not null && record.RoutingKey != routingKey) continue;
            T? value;
            try { value = JsonSerializer.Deserialize<T>(record.Body, Options.JsonSerializerOptions); }
            catch (JsonException) { continue; }
            if (value is not null) yield return new(value, record);
        }
    }

    public Task OnTestStartedAsync(StoveTestContext test) { _store.Start(test); return Task.CompletedTask; }
    public Task OnTestEndedAsync(StoveTestContext test, Exception? failure) { _store.End(test, failure); return Task.CompletedTask; }
    public Task<FailureDetails?> DescribeAsync(StoveTestContext test, CancellationToken cancellationToken)
    {
        var snapshot = _store.Inspect(test);
        var lines = snapshot.Records.TakeLast(50).Select(r => $"  {r.Exchange}/{r.RoutingKey}: {r.Body.Length} bytes, message id={r.MessageId}");
        return Task.FromResult<FailureDetails?>(new(Name is null ? "rabbitmq" : $"rabbitmq '{Name}'",
            $"Observed {snapshot.Records.Count} messages on {ObservationQueue ?? "(no bindings)"}.\n{snapshot.Error}\n" + string.Join("\n", lines)));
    }

    private void ObserverFailed(string error) { if (Volatile.Read(ref _disposed) == 0) _store.Fail("RabbitMQ " + error); }
    private async ValueTask CleanupAsync()
    {
        if (_connection is null || Options.Cleanup is null) return;
        await using var channel = await _connection.CreateChannelAsync().ConfigureAwait(false);
        await Options.Cleanup(new(_connection, channel, ExposedConfiguration), CancellationToken.None).ConfigureAwait(false);
    }

    public override async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        await DisposeResourcesAsync(CleanupAsync,
            () => _observer?.DisposeAsync() ?? ValueTask.CompletedTask,
            () => _publisher?.DisposeAsync() ?? ValueTask.CompletedTask,
            () => _connection?.DisposeAsync() ?? ValueTask.CompletedTask,
            () => { _publishLock.Dispose(); return ValueTask.CompletedTask; },
            () => _container?.DisposeAsync() ?? ValueTask.CompletedTask).ConfigureAwait(false);
    }
}

public static class RabbitMqStoveExtensions
{
    public static StoveBuilder WithRabbitMq(this StoveBuilder builder, Action<RabbitMqOptions>? configure = null) => builder.WithRabbitMq(null, configure);
    public static StoveBuilder WithRabbitMq(this StoveBuilder builder, string? name, Action<RabbitMqOptions>? configure = null)
    {
        var options = new RabbitMqOptions();
        configure?.Invoke(options);
        return builder.WithSystem(new RabbitMqSystem(name, options));
    }
    public static RabbitMqSystem RabbitMq(this StoveTestContext test, string? name = null) => test.GetSystem<RabbitMqSystem>(name);
}
