# Telemetry and failure reports

`WithTelemetry()` starts an OTLP/HTTP (protobuf) receiver and exposes the standard SDK settings as configuration:
`OTEL_EXPORTER_OTLP_ENDPOINT`, `OTEL_EXPORTER_OTLP_PROTOCOL=http/protobuf`, and short batch delays. An application with
a standard OpenTelemetry setup exports to it without changes:

```csharp
builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService("order-service"))
    .WithTracing(t => t.AddAspNetCoreInstrumentation().AddHttpClientInstrumentation().AddNpgsql().AddOtlpExporter())
    .WithLogging(l => l.AddOtlpExporter());
```

Each test gets its own W3C trace. Stove's HTTP calls and Kafka messages carry `traceparent` and `X-Stove-Test-Id`, so
the application's spans and logs join the test's trace. When a test fails, Stove rethrows a `StoveTestFailedException`.
The original exception is kept as `InnerException`, and the message contains what each system observed:

```text
StoveDotnet.StoveTestFailedException : EqualException: Assert.Equal() Failure: Values differ
Expected: Created
Actual:   PaymentRequired

Stove test: OrderTests.Failure_output_demo
Trace id:   b23be8492d7e283bae0f7b389e58a5fc

--- Stove: application:(default) / application logs ---
09:42:49.016 [Warning] OrderService.OrderWorkflow: Payment declined for order 6de533c8-... of customer customer-4

--- Stove: telemetry ---
[x] POST http://127.0.0.1:58643/orders (order-service, Client, 356.8ms)
`-- [ok] POST /orders (order-service, Server, 347.8ms)
    |-- [ok] GET http://localhost:58630/stock/sofa (order-service, Client, 260.8ms)
    |   `-- [ok] GET (order-service, Server, 246.7ms)
    `-- [x] POST http://localhost:58631/charges (order-service, Client, 29.2ms)
        `-- [x] POST (order-service, Server, 25.8ms)

--- Stove: wiremock 'payments' ---
Requests received (1):
  POST http://localhost:58631/charges -> 500 (matched) {"orderId":"6de533c8-...","customerId":"customer-4","amount":10}

--- Stove: http ---
Requests sent (1):
  POST http://127.0.0.1:58643/orders → 402 PaymentRequired (356.8ms)
    Response body: (empty)
```

The report does not depend on how the test asserted. `http` lists the requests the test sent with their status and a
bounded, redacted response body, so a plain `Assert.Equal(HttpStatusCode.Created, response.StatusCode)` still shows
what the application answered. `application logs` come from the in-process host and need no OpenTelemetry setup:
the application's own categories from Information, framework categories from Warning, with exception stack traces.
Warnings and errors written outside any trace while the test ran (startup, background services) are listed separately
and marked as not test-correlated. The telemetry section shows the trace tree; set
`TelemetryOptions.IncludeLogsInFailureDetails` to also list OTLP logs there.

The application runs in the test process, so instrumentation sees everything in that process. The server spans under
the WireMock calls above come from the in-process WireMock servers.

## Correlation and parallel tests

Kafka, RabbitMQ and Azure Service Bus accept messages carrying the active test's id or a valid matching `traceparent`. If both headers
are present, both must agree. Malformed or conflicting headers are excluded. Headerless messages are excluded by
default; the explicit `SingleActiveTest` fallback uses observer arrival time and cannot distinguish a delayed old
message from current work. Use strict correlation for parallel tests and existing brokers.

Both observers retain at most 10,000 messages / 16 MiB of payload/header evidence per active test by default. Retention
overflow makes assertions fail explicitly. Completed scopes release their records, with bounded failure evidence kept
long enough for diagnostics. These defaults intentionally change Kafka's previous unbounded, permissive behavior.
See [migration guidance and exact guarantees](messaging.md).

Other modules keep their existing correlation behavior; telemetry may include data without correlation headers.
Application code must propagate trace context across boundaries:

- ASP.NET Core and HttpClient instrumentation propagate W3C context.
- Kafka, RabbitMQ and Azure Service Bus producers must forward `traceparent` (or the test id) into message properties. The native-client test
  apps demonstrate this without referencing Stove.
- For WireMock stubs that differ between parallel tests, set `ScopeStubsToTest = true`.
