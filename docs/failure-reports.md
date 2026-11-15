# Telemetry and failure reports

`WithTelemetry()` listens, inside the test process, to the activities (spans) the application creates and keeps the
ones that belong to a test. The application needs no exporter, no collector and no Stove-specific code. Spans are
available as soon as they finish, so reading them does not wait for an export.

What a span contains is decided by whoever creates it:

- **HttpClient** spans carry method, URL and status code without any setup.
- **ASP.NET Core** request spans exist without setup but are bare. Register the application's usual OpenTelemetry
  instrumentation to get route names, status and exception types; no exporter is required for Stove:

```csharp
builder.Services.AddOpenTelemetry()
    .WithTracing(t => t.AddAspNetCoreInstrumentation().AddHttpClientInstrumentation().AddNpgsql());
```

- **Databases and brokers** appear when their client library or instrumentation creates activities.

If the application also configures an exporter for production, leave it as it is; Stove neither needs nor replaces it.
Stove asks for span data only inside a test's trace, so sampling of everything else is unchanged. Inside a test's
trace every span is recorded, which also means a configured exporter will export those spans.

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
[x] POST http://127.0.0.1:58643/orders (System.Net.Http, Client, 356.8ms)
`-- [ok] POST /orders (Microsoft.AspNetCore, Server, 347.8ms)
    |-- [ok] GET http://localhost:58630/stock/sofa (System.Net.Http, Client, 260.8ms)
    |   `-- [ok] GET (Microsoft.AspNetCore, Server, 246.7ms)
    `-- [x] POST http://localhost:58631/charges (System.Net.Http, Client, 29.2ms)
        `-- [x] POST (Microsoft.AspNetCore, Server, 25.8ms)

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
and marked as not test-correlated. The telemetry section shows the trace tree: each line names the span, the
activity source that created it, its kind and duration, and for a failed span the error type or message. Spans that
had started but not finished when the report was built are counted below the tree.

The application runs in the test process, so the listener sees everything in that process. The server spans under
the WireMock calls above come from the in-process WireMock servers. With several applications in one process, a span
is attributed by its place in the tree, not by an application or service name.

`t.Telemetry().Spans()` returns the finished spans of the test and waits (up to `TelemetryOptions.SettleTimeout`) for
spans that have started but not finished, such as a request handler that completes just after its response was sent.
For work the application starts later, use `ShouldContainSpan(predicate)`, which polls.

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
