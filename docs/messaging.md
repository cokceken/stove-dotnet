# Messaging observation and RabbitMQ

Kafka and RabbitMQ share the same bounded, per-test observation policy. Broker observation, routing, acknowledgements
and business processing are different facts. Neither module infers successful business processing from a publish.

## Correlation and retention

The default `Observation.UncorrelatedMessages` is `UncorrelatedMessagePolicy.Exclude`:

- A matching `X-Stove-Test-Id` or valid matching W3C `traceparent` identifies an active test.
- If both headers are present, both must match. Malformed, empty or conflicting headers are excluded.
- A message without either header is excluded. Unknown/finished test ids and records received outside active scopes
  are discarded rather than kept for future tests.
- Records belong to scopes active when Stove's observer receives them. Broker timestamps do not define test ownership.

`SingleActiveTest` is an explicit fallback for headerless messages received while exactly one scope is active.
It never assigns ambiguous records to overlapping scopes and never replays stored records into later tests. It cannot
distinguish a delayed old message received during a new single scope from that scope's current work. This also applies
to old Kafka records first received while the observer catches up. Propagate correlation for reliable isolation.

```csharp
.WithKafka(o =>
{
    o.Observation.MaxMessagesPerTest = 10_000;
    o.Observation.MaxBytesPerTest = 16 * 1024 * 1024;
    o.Observation.UncorrelatedMessages = UncorrelatedMessagePolicy.Exclude;
});
// RabbitMqOptions exposes the same Observation options.
```

Limits are captured when the system is constructed. The defaults are 10,000 messages and 16 MiB per active test.
Byte accounting includes retained bodies, keys where applicable, text headers and routing metadata; object overhead
and native-client buffers are additional. Retained memory scales with the number of active tests. These are not broker
queue limits, disk-retention settings or a bound on transient network allocations.

When either limit is exceeded, the buffer stops retaining further records for that test and marks its evidence
incomplete. Observation assertions, including absence assertions, throw `StoveAssertionException` rather than silently
using partial evidence. Already-returned results cannot be retroactively invalidated. No TTL evicts evidence from a
long-running active test. Successful scopes release their buffers; failed scopes retain bounded evidence through
failure reporting without being kept alive by the system. Later scopes begin empty.

Known observer failures also invalidate observation. RabbitMQ connection/channel closure and consumer cancellation
fail assertions instead of pretending the observation window remained complete. RabbitMQ automatic recovery is
disabled deliberately, including after the client hook runs. Kafka keeps retrying nonfatal broker errors and marks
fatal/unexpected consumer failure as incomplete observation. Broker lag and transient disconnection still limit what
a bounded observation interval can establish.

`ShouldNotBePublished(condition, within, ...)` observes for the entire positive interval. It states that no matching
record was observed within the available evidence/window. It does not prove no message exists in the broker, is waiting
behind lag, or will arrive later. `Peek` is a snapshot, not a substitute for an asynchronous absence assertion.

### Migrating existing Kafka tests

This changes Kafka's previous behavior, which retained all records for the environment lifetime and allowed
uncorrelated records to match every test. Tests using Stove's `Publish` already receive correlation headers. For
application-produced records, forward `traceparent` or the test id across producer/consumer boundaries. Do not depend
on records produced during application startup being available to a later scope.

For sequential legacy tests that cannot yet propagate headers, explicitly select `SingleActiveTest` and understand its
arrival-time limitation. Prefer narrower `ObservedTopicsPattern` and suitable limits to accommodate expected traffic.
Increasing a limit is appropriate for a known workload; there is no unlimited-retention mode.

Kafka's `ShouldBeConsumed` still means a group committed past an observed offset. It does not establish that the
handler succeeded or that a business transaction committed. `ShouldBeFailed` observes the configured error-topic
suffixes; it is not a consumer-outcome hook. This change does not alter the permissive `StoveTestContext.Owns` behavior
used by other modules such as telemetry.

## RabbitMQ setup

Reference `StoveDotnet.RabbitMq` and use `StoveDotnet.RabbitMq` and `RabbitMQ.Client` namespaces. The initial verified
combination is RabbitMQ.Client 7.2.2, Testcontainers.RabbitMq 4.15.0 and `rabbitmq:4.1-alpine` on Windows x64/Podman.
The image tag follows a version line; use an explicit tag/digest for reproducible images. Other versions and clustered
recovery scenarios need separate verification.

```csharp
var builder = StoveBuilder.Create().WithRabbitMq("orders", o =>
{
    o.ConfigureExposedConfiguration = c => [new("Rabbit:ConnectionString", c.ConnectionString)];
    o.Setup.Add(async (ctx, ct) =>
    {
        await ctx.Channel.ExchangeDeclareAsync("orders", ExchangeType.Topic, cancellationToken: ct);
        await ctx.Channel.QueueDeclareAsync("orders.work", false, false, false, cancellationToken: ct);
        await ctx.Channel.QueueBindAsync("orders.work", "orders", "orders.submit", cancellationToken: ct);
    });
    o.Bindings.Add(new RabbitMqBinding("orders", "orders.#"));
});
// Add your application and start this builder in the fixture.
```

`Setup.Add(callback, order)` prepares exchanges, queues and bindings in ascending order before the application starts.
The callback receives `Connection`, a temporary `Channel`, and `Configuration`. The setup channel is disposed after
setup; do not retain it. Setup reruns on every environment start without persisted migration history.

Stove declares a server-named, exclusive, auto-delete queue and binds it to the explicitly listed exchanges/routes.
It starts consuming before application startup. It never consumes the application's work queue. RabbitMQ binding
semantics apply: `#` is a wildcard on topic exchanges, not on direct exchanges. The default exchange cannot be observed
by adding bindings, so empty exchange names are rejected in `Bindings`. Use named exchanges for observable workflows.
With no bindings, native access and publishing still work, but observation APIs reject the missing configuration.

`ObservationQueue` exposes the queue name for diagnostics. The exclusive queue is removed when Stove's connection
closes, including on an external broker. This is a copy of future routed deliveries, not a replay of existing work-queue
messages. Adding the observer changes the set of queues bound to the exchange, which matters for mandatory routing.

| Option | Behavior |
| --- | --- |
| `Image`, `Username`, `Password` | Managed container defaults: `rabbitmq:4.1-alpine`, `stove`, `stove` |
| `ConfigureContainer` | Customize native `RabbitMqBuilder` |
| `ConfigureClient` | Customize Stove's `ConnectionFactory`, e.g. TLS or client name; automatic recovery remains disabled |
| `UseExisting(connectionString, runSetup: true)` | Connect to an external AMQP endpoint; do not create a container |
| `Setup` | Ordered topology/seed callbacks; optionally skipped on an existing endpoint |
| `Cleanup` | Optional shutdown callback with a fresh temporary channel, including on existing endpoints |
| `JsonSerializerOptions` | Defaults to System.Text.Json web settings |
| `DefaultTimeout` | Default positive assertion timeout, 10 seconds |
| `Bindings`, `Observation` | Routes to observe and per-test retention/correlation rules |

Stove validates the actual connection even when setup is disabled. It owns its connection and internal publisher and
observer channels in both runtime modes. Cleanup failure does not skip later client/container disposal, and repeated
disposal does not rerun cleanup. External servers are not stopped. User-created topology is not automatically deleted;
an explicit cleanup callback can delete it. Client customization configures Stove, not the application's client.

## RabbitMQ test APIs and guarantees

```csharp
await stove.Test(async t =>
{
    var id = Guid.NewGuid().ToString();
    await t.RabbitMq("orders").Publish("orders", "orders.submit", new { id, value = "hello" });
    var message = await t.RabbitMq("orders").ShouldBePublished<JsonElement>(
        m => m.Value.GetProperty("id").GetString() == id, routingKey: "orders.processed");
    // Verify the application's business effect through HTTP, storage or another observable boundary.
});
```

- `Publish<T>(exchange, routingKey, message, headers?, mandatory: true)` serializes JSON, adds test correlation and
  waits for broker publisher confirmation. Native return/nack exceptions propagate. A mandatory unroutable message
  produces `PublishReturnException` with the selected client. Custom headers can override correlation explicitly.
- `PublishRaw(exchange, routingKey, body, headers?, mandatory: true, contentType: "application/octet-stream")` sends
  native bytes. Use `Connection` and native channels for further protocol properties or custom serialization.
- `ShouldBePublished<T>(condition, timeout?, exchange?, routingKey?)` waits for a JSON-decodable delivery copy on Stove's
  queue. `RabbitMqMessage<T>.Record` includes exchange, routing key, copied body, text headers, message id and observation
  timestamp. Non-text AMQP header values appear as `(non-text)` in the diagnostic record.
- `ShouldNotBePublished<T>(condition, within, exchange?, routingKey?)` and `Peek<T>(exchange?, routingKey?)` use the same
  scoped evidence and exact exchange/routing-key filters. Those filters are not binding patterns.
- `Connection` exposes native `IConnection`. The caller owns channels created from it and passes cancellation tokens.
  Do not dispose Stove's connection during normal tests. Stove serializes concurrent calls on its own publisher channel.

A publisher confirm establishes broker acceptance. Mandatory success establishes a route to at least one queue,
possibly only Stove's queue. Observing a copy establishes neither application delivery nor acknowledgement. There is
intentionally no RabbitMQ `ShouldBeConsumed` assertion guessing at processing. The acceptance app demonstrates success
with an HTTP-readable side effect and rejection with a separate event. See RabbitMQ's
[native client guide](https://www.rabbitmq.com/client-libraries/dotnet-api-guide) for channel ownership and confirmation APIs.

The independent acceptance suite covers real-app publication and processing, ordered setup, routing returns, observer
isolation, named instances, concurrent scopes, existing endpoints, cancellation, incomplete evidence, observer loss,
and startup/cleanup failure. OrderService stays focused on its existing composition.
