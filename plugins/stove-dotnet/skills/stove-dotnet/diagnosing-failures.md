# Diagnosing StoveDotnet failures

## Reading a StoveTestFailedException

```text
StoveDotnet.StoveTestFailedException : EqualException: Assert.Equal() Failure: Values differ   <- original failure
Expected: Created
Actual:   PaymentRequired

Stove test: OrderTests.Failure_output_demo                                                      <- file.method
Trace id:   b23be8492d7e283bae0f7b389e58a5fc

--- Stove: telemetry ---
[x] POST http://127.0.0.1:58643/orders (order-service, Client, 356.8ms)                        <- the test's own call
`-- [ok] POST /orders (order-service, Server, 347.8ms)                                         <- the app handling it
    |-- [ok] GET http://localhost:58630/stock/sofa (order-service, Client, 260.8ms)            <- app -> third party
    |   `-- [ok] GET (order-service, Server, 246.7ms)                                          <- in-process WireMock
    `-- [x] POST http://localhost:58631/charges (order-service, Client, 29.2ms)
        `-- [x] POST (order-service, Server, 25.8ms)

Logs (1):
  09:42:49.016 [Warning] OrderService.OrderWorkflow: Payment declined for order 6de5... of customer customer-4

--- Stove: kafka ---
Messages observed (0):

--- Stove: wiremock 'payments' ---
Requests received (1):
  POST http://localhost:58631/charges -> 503 (matched) {"orderId":"6de5...","customerId":"customer-4","amount":10}
```

Read it top-down:

1. **The original exception** (first line, and `InnerException`) says what was expected.
2. **The trace tree** shows what the app actually did for this test's trace:
   - `[x]` marks error spans; client spans show the called URL;
   - nesting shows causality;
   - server spans nested under third-party calls come from the in-process WireMock servers.
3. **Logs** are the app's logs within the trace. Look for warnings and errors near the failing step.
4. **Module sections** list what each system saw for this test:
   - WireMock requests with status, `matched`/`UNMATCHED` and bodies;
   - Kafka messages with topic, offset, key and value.

`StoveTimeoutException` messages describe what was observed when the wait ended (messages seen, committed offsets,
spans received).

## Symptoms

| Symptom | Likely cause | Fix |
|---|---|---|
| `(no spans received)` in every test | App has no OpenTelemetry OTLP exporter, `WithTelemetry()` missing, or the app hard-codes an exporter endpoint | Add `AddOtlpExporter()` without an explicit endpoint; register `WithTelemetry()` (see `setup.md`) |
| Spans exist but not under the test | App code starts work outside the request (fire-and-forget without `Activity` flow), or a Kafka consumer does not restore `traceparent` | Flow `Activity.Current`; start consumer activities from the message's `traceparent` |
| WireMock `UNMATCHED` request | Stub path, method or body differs from what the app sent, or the stub was registered in another test | Compare the logged URL and body with the stub. `requestBody:` is a JSON equality match, so drop it or stub via `Stub(...)` with partial matchers |
| WireMock `Requests received: (none)` but the app called it | The app's base URL key is not mapped (the call went elsewhere), or the call is made asynchronously after the assertion | Check `ConfigureExposedConfiguration` uses the key the app reads; pass `within:` |
| `expected ... called 1 time(s) but it was called 2` | Retries in the app's HTTP resilience pipeline, or duplicate calls | Assert the real count, or stub a success so no retry happens |
| `ShouldBeConsumed` times out, "published but not consumed" | Wrong `consumerGroup`, the consumer never commits (auto-commit interval, manual commit missing), topic not subscribed, or consumer crashed | Check the committed offsets listed in the message; check app logs for consumer errors |
| `ShouldBePublished` times out, "Observed: ..." lists messages | Predicate mismatch or deserialization failure (casing, types) | Compare the observed JSON with `T`; set `KafkaOptions.Serde` to match the app's serializer |
| `No messages were observed for this test` with parallel tests | App-produced messages carry another trace (no `traceparent` propagation) | Propagate `traceparent`; data without headers matches all tests |
| `Configuration key 'X' is exposed by more than one system` | Two systems map the same key | Map each system to its own key |
| `Multiple XSystem instances are registered and none is unnamed` | `t.WireMock()` without a name while several named instances exist | Pass the name or use the typed fake accessor |
| `This Stove operation must be called inside stove.Test(...)` | DSL used in a constructor, fixture or after the test body returned (for example an un-awaited task) | Move the call into the body and await everything |
| `Stove tests cannot be nested` | `stove.Test` called inside another `stove.Test` | One `stove.Test` per test method |
| Startup: the app throws about a missing configuration value | The app reads a key Stove does not provide | Map it through a system's `ConfigureExposedConfiguration`, or `ConfigureWebHost(b => b.UseSetting(...))` |
| Build errors `CS0246`/`CS0436` in files under `ramltoopenapiconverter.sourceonly/.../contentFiles` | The project references `WireMock.Net` or `WireMock.Net.Minimal` 2.15 **directly**; an upstream dependency leaks C# sources into consumers | Remove the direct reference and use `StoveDotnet.WireMock`, which strips them (see its `buildTransitive/StoveDotnet.WireMock.targets`), or copy that target into the project |
| Docker errors at startup (`Cannot connect`, pull failures) | Container runtime not running or unreachable | Start Docker or Podman. Podman: make sure the Docker-compatible API socket or pipe is enabled, and give Kafka at least 4 GB |
| Kafka container killed or tests hang at startup | Low memory for the container VM | Increase the Docker/Podman VM memory |
| `Could not load file or assembly ... Application Control policy has blocked this file` (Windows) | Smart App Control or WDAC blocking unsigned test DLLs | Machine policy issue, not a Stove bug. Ask the user; do not change security settings yourself |

## Useful probes while debugging

```csharp
Console.WriteLine(t.Telemetry().RenderTree());
foreach (var r in t.WireMock("payments").Requests()) Console.WriteLine($"{r} {r.Body}");
foreach (var m in t.Kafka().Peek<JsonElement>()) Console.WriteLine($"{m.Topic}@{m.Record.Offset} {m.Record.ValueAsString}");
```

Remove the probes once the cause is found.

## Incomplete Kafka/RabbitMQ observation

If a failure reports a retention limit, evidence was deliberately bounded and later records were not retained.
Narrow observed Kafka topics/RabbitMQ bindings or increase `Observation.MaxMessagesPerTest` / `MaxBytesPerTest` for the
known workload. An absence assertion must not pass after evidence loss. Failed scopes retain bounded diagnostics;
completed scopes do not provide history to later tests.

If a message is missing, check correlation first: matching test id or valid traceparent is required by default, both
must agree when present, and malformed headers are excluded. `SingleActiveTest` fallback is explicit and uses arrival
time, so it cannot safely distinguish delayed previous work. RabbitMQ assertions also require explicit bindings to
named exchanges. A cancelled/lost observer fails assertions; restart the environment to restore complete observation.
RabbitMQ failure details show routes, sizes and message ids without including payload bodies.
