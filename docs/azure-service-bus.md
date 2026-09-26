# Azure Service Bus

`StoveDotnet.Azure.ServiceBus` runs the official Azure Service Bus emulator or connects to an existing namespace. It
keeps Azure-specific behavior visible: queues, topics, subscriptions, rules, scheduled broker state, peek-lock
settlement and native SDK clients are not hidden behind a generic messaging abstraction.

The managed emulator defaults to the pinned `servicebus-emulator:2.0.0` image, depends on SQL Server, accepts only connection-string authentication and requires explicit
acceptance of Microsoft's emulator license. It is development/test infrastructure, not a production-compatible Azure
environment. Microsoft Entra ID, managed identity, partitioned entities, AMQP WebSockets and several Azure platform
features are unavailable in the emulator. Use an existing Azure namespace to verify authentication and RBAC behavior.

## Managed emulator and typed topology

```csharp
var stove = await StoveBuilder.Create()
    .WithAzureServiceBus(o =>
    {
        o.AcceptLicenseAgreement = true;
        o.Topology
            .Queue("commands", q =>
            {
                q.MaxDeliveryCount = 5;
                q.LockDuration = TimeSpan.FromMinutes(1);
            })
            .Topic("events", subscriptions: topic => topic
                .Subscription("billing", rules: rules => rules
                    .Sql("invoice-created", "eventType = 'InvoiceCreated'")));
        o.ConfigureExposedConfiguration = c =>
            [new("ServiceBus:ConnectionString", c.ConnectionString)];
    })
    .WithAspNetCoreApplication<Program>()
    .StartAsync();
```

Topology uses the Azure SDK's `CreateQueueOptions`, `CreateTopicOptions`, `CreateSubscriptionOptions` and
`CreateRuleOptions`. Missing entities and rules are created before applications start; existing entities are not
silently updated. `Setup` adds ordered native callbacks after typed topology. The emulator container is destroyed with
the environment. Stable topology is shared by all test scopes and is not purged between tests.

## Existing namespaces and passwordless authentication

```csharp
.WithAzureServiceBus(o => o.UseExisting(
    "orders.servicebus.windows.net",
    new DefaultAzureCredential(),
    runTopologySetup: false))
```

The overload accepts any `Azure.Core.TokenCredential`, including `DefaultAzureCredential`, managed/workload identity
and service-principal credentials. Stove passes it directly to the Azure SDK; it never reads, caches or reports access
tokens and does not dispose a caller-owned credential. `ServiceBusClient` and `AdministrationClient` are owned by Stove.

Stove's credential configures Stove's client only. The real application still constructs its own production client.
Expose `FullyQualifiedNamespace` to applications using passwordless authentication. For the emulator, expose
`ConnectionString`; the application should select connection string when present and namespace plus its normal
credential otherwise.

`runTopologySetup: false` is the safe default for existing namespaces. Sending requires the Azure Service Bus Data
Sender role, receiving requires Data Receiver, and topology administration generally requires Data Owner. A
least-privileged production application should not be granted ownership merely to make tests convenient. Token/RBAC
behavior requires an opt-in test against real Azure because the emulator does not support Entra ID.

## Publishing, scheduling and consuming

```csharp
await stove.Test(async t =>
{
    await t.AzureServiceBus().Queue("commands").Publish(new StartOrder(orderId));
    await Eventually.AssertAsync(
        async ct => await AssertBusinessEffect(orderId, ct),
        TimeSpan.FromSeconds(10),
        cancellationToken: t.CancellationToken);
});
```

`Publish` adds `traceparent` and `X-Stove-Test-Id`. The application is the consumer in this pattern; prove successful
processing through HTTP, storage, telemetry or another business-visible output. A message being absent, locked or
settled does not prove that its handler committed successfully, so there is intentionally no `ShouldBeConsumed` API.

Scheduled publication is inspected immediately without advancing or waiting for the broker's clock:

```csharp
var due = DateTimeOffset.UtcNow.AddDays(7);
await t.AzureServiceBus().Queue("reminders").Schedule(new SendReminder(id), due);
var scheduled = await t.AzureServiceBus().Queue("reminders")
    .ShouldBeScheduled<SendReminder>(m => m.Value.Id == id);
Assert.Equal(due, scheduled.NativeMessage.ScheduledEnqueueTime, TimeSpan.FromSeconds(1));
```

`Peek<T>` and `ShouldBeScheduled<T>` inspect broker state non-destructively and apply strict correlation. Peek scans at
most `MaxPeekMessages` (1,000 by default); this is a bounded assertion, not proof about messages outside that page.
Scheduled messages sent through Stove or discovered by `ShouldBeScheduled` are cancelled when their test ends by
default, preventing a long-delay test message from activating in a later scope. Set
`CancelScheduledMessagesAfterTest = false` only when retention is intentional.

Stove can act as the external consumer of an application-produced message:

```csharp
await using var delivery = await t.AzureServiceBus()
    .Subscription("events", "billing")
    .Receive<InvoiceCreated>();
Assert.Equal(invoiceId, delivery.Value.InvoiceId);
await delivery.Complete(); // or Abandon, DeadLetter, Defer
```

`Receive` is destructive and uses peek-lock. The delivery exposes the native message and requires explicit settlement;
disposing an unsettled delivery attempts to abandon it. Native senders and receivers created from `Client` are owned by
the caller.

## Correlation and parallelism

Strict matching follows Kafka and RabbitMQ: a matching test id or valid matching traceparent identifies a test, both
must agree when present, and malformed/conflicting properties are excluded. Headerless messages are excluded unless
`UncorrelatedMessages = SingleActiveTest`; that fallback is unsuitable for overlapping tests.

Parallel execution is supported for correlated scenarios, not for arbitrary shared broker state:

- Multiple tests may publish correlated messages to one application-consumed queue when business data is unique and
  the application propagates correlation to downstream messages.
- Scheduled peeking can overlap because it is non-destructive and correlation filters the result.
- `Receive` must not share a queue/subscription with the application or another overlapping test receiver. Service Bus
  locks messages before client-side correlation can inspect them; correlation cannot prevent competing consumers.
- Queue counts, FIFO order, duplicate detection, sessions, delivery counts, dead-letter state and entity-wide cleanup
  are shared. Serialize those tests or use separate entities/environments. Use unique `SessionId` and `MessageId`
  values when those features are exercised concurrently.
- Do not purge a shared entity for per-test cleanup. Stable topology plus correlation avoids assertion crossover, but
  cannot undo business effects caused by an application consuming another test's message.

Microsoft characterizes the emulator as primarily intended for sequential testing and limits a namespace to ten
connections. Stove verifies overlapping correlated peeks, but does not claim that every emulator feature or topology
is parallel-safe. Run fragile session/ordering/emulator-lifecycle cases sequentially.
