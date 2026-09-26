using System.Text.Json;
using Azure.Messaging.ServiceBus;

namespace StoveDotnet.Azure.ServiceBus;

public sealed record AzureServiceBusMessage<T>(T Value, ServiceBusReceivedMessage NativeMessage);

public sealed class AzureServiceBusEntity
{
    private readonly AzureServiceBusSystem _system;
    private readonly string _entity;
    private readonly string? _subscription;
    private readonly bool _canSend;

    internal AzureServiceBusEntity(AzureServiceBusSystem system, string entity, string? subscription, bool canSend)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entity);
        if (subscription is not null) ArgumentException.ThrowIfNullOrWhiteSpace(subscription);
        _system = system;
        _entity = entity;
        _subscription = subscription;
        _canSend = canSend;
    }

    public async Task Publish<T>(T value, IDictionary<string, object>? applicationProperties = null)
        => await Publish(_system.CreateMessage(value, applicationProperties)).ConfigureAwait(false);

    /// <summary>Sends a native message after applying this test's correlation properties.</summary>
    public async Task Publish(ServiceBusMessage message)
    {
        EnsureCanSend();
        ArgumentNullException.ThrowIfNull(message);
        var test = StoveTestContext.Require();
        using var activity = test.StartCorrelatedActivity("stove.azure.servicebus.publish");
        ApplyCorrelation(message, test);
        await using var sender = _system.Client.CreateSender(_entity);
        await sender.SendMessageAsync(message, test.CancellationToken).ConfigureAwait(false);
    }

    public async Task<long> Schedule<T>(T value, DateTimeOffset enqueueAt, IDictionary<string, object>? applicationProperties = null)
        => await Schedule(_system.CreateMessage(value, applicationProperties), enqueueAt).ConfigureAwait(false);

    /// <summary>Schedules a native message after applying this test's correlation properties.</summary>
    public async Task<long> Schedule(ServiceBusMessage message, DateTimeOffset enqueueAt)
    {
        EnsureCanSend();
        ArgumentNullException.ThrowIfNull(message);
        var test = StoveTestContext.Require();
        using var activity = test.StartCorrelatedActivity("stove.azure.servicebus.schedule");
        ApplyCorrelation(message, test);
        await using var sender = _system.Client.CreateSender(_entity);
        var sequenceNumber = await sender.ScheduleMessageAsync(
            message, enqueueAt, test.CancellationToken).ConfigureAwait(false);
        _system.TrackScheduled(_entity, sequenceNumber, test);
        return sequenceNumber;
    }

    /// <summary>Non-destructively peeks correlated broker state. Results are bounded by MaxPeekMessages.</summary>
    public async Task<IReadOnlyList<AzureServiceBusMessage<T>>> Peek<T>()
    {
        var test = StoveTestContext.Require();
        await using var receiver = CreateReceiver();
        var messages = await receiver.PeekMessagesAsync(_system.Options.MaxPeekMessages, 0, test.CancellationToken).ConfigureAwait(false);
        return messages.Where(m => _system.Owns(m, test)).Select(Deserialize<T>).Where(m => m is not null).Cast<AzureServiceBusMessage<T>>().ToArray();
    }

    public async Task<AzureServiceBusMessage<T>> ShouldBeScheduled<T>(
        Func<AzureServiceBusMessage<T>, bool> condition,
        TimeSpan? timeout = null)
    {
        EnsureCanSend();
        ArgumentNullException.ThrowIfNull(condition);
        var test = StoveTestContext.Require();
        AzureServiceBusMessage<T>? match = null;
        await Eventually.UntilAsync(async ct =>
        {
            await using var receiver = CreateReceiver();
            var messages = await receiver.PeekMessagesAsync(_system.Options.MaxPeekMessages, 0, ct).ConfigureAwait(false);
            match = messages.Where(m => m.State == ServiceBusMessageState.Scheduled && _system.Owns(m, test))
                .Select(Deserialize<T>).Where(m => m is not null).Cast<AzureServiceBusMessage<T>>().FirstOrDefault(condition);
            return match is not null;
        }, timeout ?? _system.Options.DefaultTimeout,
        () => $"No correlated scheduled {typeof(T).Name} matching the condition was found on '{_entity}'.",
        cancellationToken: test.CancellationToken).ConfigureAwait(false);
        _system.TrackScheduled(_entity, match!.NativeMessage.SequenceNumber, test);
        return match;
    }

    /// <summary>
    /// Destructively receives the next message in peek-lock mode. Do not share this entity with another test receiver
    /// or the application under test; correlation cannot prevent competing consumers from acquiring the lock first.
    /// </summary>
    public async Task<AzureServiceBusDelivery<T>> Receive<T>(TimeSpan? timeout = null)
    {
        var test = StoveTestContext.Require();
        var receiver = CreateReceiver();
        try
        {
            var message = await receiver.ReceiveMessageAsync(timeout ?? _system.Options.DefaultTimeout, test.CancellationToken).ConfigureAwait(false)
                ?? throw new StoveTimeoutException($"No message was received from '{Path}'.");
            if (!_system.Owns(message, test))
            {
                await receiver.AbandonMessageAsync(message, cancellationToken: test.CancellationToken).ConfigureAwait(false);
                throw new StoveAssertionException($"The next message on '{Path}' belongs to another or no test. Receive is destructive and requires an exclusive entity when tests overlap.");
            }
            var decoded = Deserialize<T>(message) ?? throw new StoveAssertionException($"Message '{message.MessageId}' on '{Path}' is not valid {typeof(T).Name} JSON.");
            return new AzureServiceBusDelivery<T>(decoded.Value, message, receiver, test.CancellationToken);
        }
        catch
        {
            await receiver.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private ServiceBusReceiver CreateReceiver()
    {
        var options = new ServiceBusReceiverOptions { ReceiveMode = ServiceBusReceiveMode.PeekLock };
        return _subscription is null
            ? _system.Client.CreateReceiver(_entity, options)
            : _system.Client.CreateReceiver(_entity, _subscription, options);
    }

    private AzureServiceBusMessage<T>? Deserialize<T>(ServiceBusReceivedMessage message)
    {
        try
        {
            var value = JsonSerializer.Deserialize<T>(message.Body.ToMemory().Span, _system.Options.JsonSerializerOptions);
            return value is null ? null : new(value, message);
        }
        catch (JsonException) { return null; }
    }

    private string Path => _subscription is null ? _entity : $"{_entity}/Subscriptions/{_subscription}";
    private static void ApplyCorrelation(ServiceBusMessage message, StoveTestContext test)
    {
        foreach (var (key, value) in test.CorrelationHeaders()) message.ApplicationProperties[key] = value;
    }

    private void EnsureCanSend()
    {
        if (!_canSend) throw new InvalidOperationException($"'{Path}' is a subscription and cannot be used as a send target.");
    }
}

public sealed class AzureServiceBusDelivery<T> : IAsyncDisposable
{
    private readonly ServiceBusReceiver _receiver;
    private readonly CancellationToken _cancellationToken;
    private int _settled;

    internal AzureServiceBusDelivery(T value, ServiceBusReceivedMessage nativeMessage, ServiceBusReceiver receiver, CancellationToken cancellationToken)
    {
        Value = value;
        NativeMessage = nativeMessage;
        _receiver = receiver;
        _cancellationToken = cancellationToken;
    }

    public T Value { get; }
    public ServiceBusReceivedMessage NativeMessage { get; }

    public Task Complete() => Settle(ct => _receiver.CompleteMessageAsync(NativeMessage, ct));
    public Task Abandon(IDictionary<string, object>? propertiesToModify = null) =>
        Settle(ct => _receiver.AbandonMessageAsync(NativeMessage, propertiesToModify, ct));
    public Task DeadLetter(string? reason = null, string? description = null) =>
        Settle(ct => _receiver.DeadLetterMessageAsync(NativeMessage, reason, description, ct));
    public Task Defer(IDictionary<string, object>? propertiesToModify = null) =>
        Settle(ct => _receiver.DeferMessageAsync(NativeMessage, propertiesToModify, ct));

    private async Task Settle(Func<CancellationToken, Task> action)
    {
        if (Interlocked.CompareExchange(ref _settled, -1, 0) != 0)
            throw new InvalidOperationException("The Service Bus delivery is already settled or settlement is in progress.");
        try
        {
            await action(_cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref _settled, 1);
        }
        catch
        {
            Volatile.Write(ref _settled, 0);
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.CompareExchange(ref _settled, 1, 0) == 0)
        {
            try { await _receiver.AbandonMessageAsync(NativeMessage, cancellationToken: CancellationToken.None).ConfigureAwait(false); }
            catch (ServiceBusException) { }
        }
        await _receiver.DisposeAsync().ConfigureAwait(false);
    }
}
