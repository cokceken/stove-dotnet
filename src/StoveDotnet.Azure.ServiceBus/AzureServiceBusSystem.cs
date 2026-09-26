using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Azure.Core;
using Azure.Messaging.ServiceBus;
using Azure.Messaging.ServiceBus.Administration;
using Testcontainers.ServiceBus;

namespace StoveDotnet.Azure.ServiceBus;

/// <summary>
/// Azure Service Bus backed by the official emulator or an existing namespace. Broker presence and successful
/// application processing are separate facts; prove processing through a business-visible effect.
/// </summary>
public sealed class AzureServiceBusSystem
    : ExposingSystem<AzureServiceBusOptions, AzureServiceBusExposedConfiguration>, IRunAware, ITestScopeAware
{
    private readonly Lock _scopeGate = new();
    private readonly HashSet<string> _activeTests = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<ScheduledMessage, byte>> _scheduled = new(StringComparer.Ordinal);
    private ServiceBusContainer? _container;
    private ServiceBusClient? _client;
    private ServiceBusAdministrationClient? _administrationClient;
    private AzureServiceBusSetupContext? _setupContext;
    private int _disposed;

    public AzureServiceBusSystem(string? name, AzureServiceBusOptions options) : base(name, options)
    {
    }

    /// <summary>Owned and disposed by Stove. Senders and receivers created by callers remain caller-owned.</summary>
    public ServiceBusClient Client => _client ?? throw new InvalidOperationException($"{DisplayName} is not running yet.");
    public ServiceBusAdministrationClient AdministrationClient =>
        _administrationClient ?? throw new InvalidOperationException($"{DisplayName} is not running yet.");

    public AzureServiceBusEntity Queue(string name) => new(this, name, null, canSend: true);
    public AzureServiceBusEntity Topic(string name) => new(this, name, null, canSend: true);
    public AzureServiceBusEntity Subscription(string topic, string subscription) => new(this, topic, subscription, canSend: false);

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(Options.MaxPeekMessages);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(Options.DefaultTimeout, TimeSpan.Zero);
        if (!Enum.IsDefined(Options.UncorrelatedMessages)) throw new InvalidOperationException("UncorrelatedMessages is not a defined policy.");

        var clientOptions = new ServiceBusClientOptions();
        Options.ConfigureClient?.Invoke(clientOptions);
        var adminOptions = new ServiceBusAdministrationClientOptions();
        Options.ConfigureAdministrationClient?.Invoke(adminOptions);

        string? connectionString = Options.ExistingConnectionString;
        string fullyQualifiedNamespace;
        var isEmulator = Options.ExistingConnectionString is null && Options.ExistingFullyQualifiedNamespace is null;

        if (isEmulator)
        {
            if (!Options.AcceptLicenseAgreement)
                throw new InvalidOperationException("Set AzureServiceBusOptions.AcceptLicenseAgreement = true after reviewing the Azure Service Bus emulator license.");
            var builder = new ServiceBusBuilder(Options.Image).WithAcceptLicenseAgreement(true);
            _container = (Options.ConfigureContainer?.Invoke(builder) ?? builder).Build();
            await _container.StartAsync(cancellationToken).ConfigureAwait(false);
            connectionString = _container.GetConnectionString();
            _client = new ServiceBusClient(connectionString, clientOptions);
            _administrationClient = new ServiceBusAdministrationClient(_container.GetHttpConnectionString(), adminOptions);
            fullyQualifiedNamespace = _client.FullyQualifiedNamespace;
        }
        else if (connectionString is not null)
        {
            _client = new ServiceBusClient(connectionString, clientOptions);
            _administrationClient = new ServiceBusAdministrationClient(connectionString, adminOptions);
            fullyQualifiedNamespace = _client.FullyQualifiedNamespace;
        }
        else
        {
            fullyQualifiedNamespace = Options.ExistingFullyQualifiedNamespace!;
            TokenCredential credential = Options.ExistingCredential!;
            _client = new ServiceBusClient(fullyQualifiedNamespace, credential, clientOptions);
            _administrationClient = new ServiceBusAdministrationClient(fullyQualifiedNamespace, credential, adminOptions);
        }

        var exposed = new AzureServiceBusExposedConfiguration(fullyQualifiedNamespace, connectionString, isEmulator);
        Expose(exposed);
        _setupContext = new AzureServiceBusSetupContext(Client, AdministrationClient, exposed);
        if (isEmulator || Options.RunTopologySetupOnExisting)
        {
            await EnsureTopologyAsync(cancellationToken).ConfigureAwait(false);
            await Options.Setup.RunAsync(_setupContext, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task EnsureTopologyAsync(CancellationToken cancellationToken)
    {
        foreach (var queue in Options.Topology.Queues)
        {
            if (!(await AdministrationClient.QueueExistsAsync(queue.Options.Name, cancellationToken).ConfigureAwait(false)).Value)
                await AdministrationClient.CreateQueueAsync(queue.Options, cancellationToken).ConfigureAwait(false);
        }

        foreach (var topic in Options.Topology.Topics)
        {
            if (!(await AdministrationClient.TopicExistsAsync(topic.Options.Name, cancellationToken).ConfigureAwait(false)).Value)
                await AdministrationClient.CreateTopicAsync(topic.Options, cancellationToken).ConfigureAwait(false);

            foreach (var subscription in topic.Subscriptions)
            {
                var topicName = topic.Options.Name;
                var subscriptionName = subscription.Options.SubscriptionName;
                if (!(await AdministrationClient.SubscriptionExistsAsync(topicName, subscriptionName, cancellationToken).ConfigureAwait(false)).Value)
                {
                    if (subscription.Rules.Count == 0)
                    {
                        await AdministrationClient.CreateSubscriptionAsync(subscription.Options, cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        await AdministrationClient.CreateSubscriptionAsync(subscription.Options, subscription.Rules[0], cancellationToken).ConfigureAwait(false);
                        foreach (var rule in subscription.Rules.Skip(1))
                            await AdministrationClient.CreateRuleAsync(topicName, subscriptionName, rule, cancellationToken).ConfigureAwait(false);
                    }
                }
                else
                {
                    var existing = new HashSet<string>(StringComparer.Ordinal);
                    await foreach (var rule in AdministrationClient.GetRulesAsync(topicName, subscriptionName, cancellationToken).ConfigureAwait(false))
                        existing.Add(rule.Name);
                    foreach (var rule in subscription.Rules.Where(r => !existing.Contains(r.Name)))
                        await AdministrationClient.CreateRuleAsync(topicName, subscriptionName, rule, cancellationToken).ConfigureAwait(false);
                }
            }
        }
    }

    internal ServiceBusMessage CreateMessage<T>(T value, IDictionary<string, object>? applicationProperties)
    {
        var test = StoveTestContext.Require();
        var message = new ServiceBusMessage(JsonSerializer.SerializeToUtf8Bytes(value, Options.JsonSerializerOptions))
        {
            ContentType = "application/json",
            MessageId = Guid.NewGuid().ToString("N")
        };
        foreach (var (key, header) in test.CorrelationHeaders()) message.ApplicationProperties[key] = header;
        foreach (var (key, property) in applicationProperties ?? new Dictionary<string, object>()) message.ApplicationProperties[key] = property;
        return message;
    }

    internal bool Owns(ServiceBusReceivedMessage message, StoveTestContext test)
    {
        var hasTestId = message.ApplicationProperties.TryGetValue(StoveHeaders.TestId, out var testIdValue);
        var hasTraceparent = message.ApplicationProperties.TryGetValue(StoveHeaders.Traceparent, out var traceValue);
        var testId = testIdValue as string;
        var traceparent = traceValue as string;
        if (hasTestId && (testId is null || string.IsNullOrWhiteSpace(testId))) return false;
        string? traceId = null;
        if (hasTraceparent && (traceparent is null || !ActivityContext.TryParse(traceparent, null, out var context))) return false;
        if (hasTraceparent) traceId = ActivityContext.Parse(traceparent!, null).TraceId.ToHexString();
        if (hasTestId || hasTraceparent)
            return (!hasTestId || testId == test.TestId) && (!hasTraceparent || traceId == test.TraceId);
        lock (_scopeGate) return Options.UncorrelatedMessages == UncorrelatedMessagePolicy.SingleActiveTest && _activeTests.Count == 1;
    }

    internal void TrackScheduled(string entity, long sequenceNumber, StoveTestContext test) =>
        _scheduled.GetOrAdd(test.TestId, _ => new()).TryAdd(new(entity, sequenceNumber), 0);

    public Task OnTestStartedAsync(StoveTestContext test)
    {
        lock (_scopeGate) _activeTests.Add(test.TestId);
        return Task.CompletedTask;
    }

    public async Task OnTestEndedAsync(StoveTestContext test, Exception? failure)
    {
        lock (_scopeGate) _activeTests.Remove(test.TestId);
        if (!_scheduled.TryRemove(test.TestId, out var messages) || !Options.CancelScheduledMessagesAfterTest) return;
        foreach (var group in messages.Keys.GroupBy(m => m.Entity, StringComparer.Ordinal))
        {
            await using var sender = Client.CreateSender(group.Key);
            foreach (var message in group)
            {
                try { await sender.CancelScheduledMessageAsync(message.SequenceNumber).ConfigureAwait(false); }
                catch (ServiceBusException e) when (e.Reason == ServiceBusFailureReason.MessageNotFound) { }
            }
        }
    }

    public override async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        await DisposeResourcesAsync(
            () => _setupContext is not null && Options.Cleanup is not null
                ? new ValueTask(Options.Cleanup(_setupContext, CancellationToken.None)) : ValueTask.CompletedTask,
            () => _client?.DisposeAsync() ?? ValueTask.CompletedTask,
            () => _container?.DisposeAsync() ?? ValueTask.CompletedTask).ConfigureAwait(false);
    }

    private sealed record ScheduledMessage(string Entity, long SequenceNumber);
}

public static class AzureServiceBusStoveExtensions
{
    public static StoveBuilder WithAzureServiceBus(this StoveBuilder builder, Action<AzureServiceBusOptions>? configure = null) =>
        builder.WithAzureServiceBus(null, configure);

    public static StoveBuilder WithAzureServiceBus(this StoveBuilder builder, string? name, Action<AzureServiceBusOptions>? configure = null)
    {
        var options = new AzureServiceBusOptions();
        configure?.Invoke(options);
        return builder.WithSystem(new AzureServiceBusSystem(name, options));
    }

    public static AzureServiceBusSystem AzureServiceBus(this StoveTestContext test, string? name = null) =>
        test.GetSystem<AzureServiceBusSystem>(name);
}
