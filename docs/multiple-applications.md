# Testing an API and a worker together

One Stove environment can start multiple named applications against shared dependencies. Each application has
its own host, service provider, configuration overrides and readiness check. All hosts run **inside the test process**;
this does not launch `dotnet run` or isolate static state, environment variables, assemblies or instrumentation.

See the [runnable API/worker example](../examples/MultiApplication/README.md). Its worker is a separate executable
project that exposes its real host factory. The API publishes to RabbitMQ; the worker persists to PostgreSQL;
the test waits until the result can be read through the API. Neither application references Stove.

## Registration and HTTP targeting

```csharp
using StoveDotnet.AspNetCore;
using StoveDotnet.Hosting;
using StoveDotnet.Http;

// Add shared dependencies and their configuration mappings before this registration chain.
builder
    .WithHostApplication("worker", WorkerApplication.Build,
        o => o.Configuration["Worker:Concurrency"] = "1")
    .WithAspNetCoreApplication<ApiProgram>("api",
        o => o.Configuration["FeatureFlags:Orders"] = "true")
    .WithHttpClient("public", o => o.ApplicationName = "api");

await stove.Test(async t =>
{
    await t.Http("public").Post("/orders", request);
    await t.Using<OrderService>("api", async service => { /* inspect real application services */ });
});
```

Client names and application names are independent: `public` above selects the client; `ApplicationName` selects
the host supplying its base address. Multiple clients may target one API with different default headers or timeouts.
An explicit `HttpClientOptions.BaseAddress` takes precedence and supports endpoints outside Stove.

Application names are case-sensitive. Lookup selects the exact name, the unnamed default, or the only application.
If several named applications exist and none is unnamed, `stove.Application`, `t.Application`, unnamed service
access and an implicitly targeted HTTP client fail with an ambiguity error. `t.GetApplication("worker").Services`
provides service access without an ASP.NET Core dependency. A worker has no HTTP base address: targeting it with an
HTTP client fails at startup. A missing application name also fails at startup and triggers rollback.

Existing `WithAspNetCoreApplication<Program>()`, `WithHttpClient()`, `t.Http()` and `t.Using<T>()` continue to work for
single-application environments. Lower-level adapters can use `WithApplication(name, adapter, options)`.

## A real worker factory

Install `StoveDotnet.Hosting` in the test project for Generic Host support. It uses Microsoft.Extensions.Hosting
and does not require ASP.NET Core. `StoveDotnet.AspNetCore` also depends on it for application log collection.
Use matching versions of Stove packages; the new Hosting package must be packed/published before external adoption.

Expose the application's own factory, applying the supplied configuration **before** reading settings and building:

```csharp
public static IHost Build(IReadOnlyDictionary<string, string?>? overrides = null)
{
    var builder = Host.CreateApplicationBuilder();
    if (overrides is not null) builder.Configuration.AddInMemoryCollection(overrides);
    builder.Services.AddHostedService<OrderWorker>();
    return builder.Build();
}

// Standalone entry point uses the same factory:
await WorkerApplication.Build().RunAsync();
```

Stove passes configuration to the factory; it cannot retroactively inject settings into a built host. There is also
an asynchronous factory overload receiving `(configuration, cancellationToken)`. Return a built, unstarted host;
Stove owns its start, stop and disposal. The factory owns cleanup of resources it creates if it throws before
returning a host. Avoid changing process-wide environment variables to pass per-application settings.

## Startup, readiness and cleanup

Dependencies start and prepare migrations/topology first. Applications then start sequentially in registration
order. Each receives a fresh copy of shared dependency configuration, overlaid with its `Configuration` dictionary.
Duplicate configuration keys exposed by dependencies still fail; application-local overrides are intentional.

`IHost.StartAsync` completing does not guarantee that a `BackgroundService` has connected its consumer or finished
initialization. Put required startup work in `IHostedService.StartAsync`, as the example does, or configure
`o.ReadyAsync = (application, ct) => WaitForConsumerReady(application.Services, ct)`. Readiness finishes before the
next application starts. Use a cancellation token/deadline for environment startup and honor it in readiness checks.
Application-to-application discovery and a dependency graph between hosts are not provided; shared dependencies
are the communication boundary in this increment.

After all applications are ready, HTTP clients bind to their targets. Shutdown attempts every application in reverse
registration order, then dependencies in reverse order. Startup/readiness failures trigger rollback, including disposal
of partially initialized adapters; adapters must safely dispose before or after startup. Generic hosts get a fresh
shutdown token (`ShutdownTimeout`, default 30 seconds) and disposal is attempted even if stopping throws.

## Failure evidence and isolation

Named startup and shutdown failures identify the application and preserve the original exception. API and worker
logs at Information or above, subject to the application's logging filters, are captured locally and attached under
`application:<name>` when their Activity trace matches the failing test. Each host retains the latest 1,000 correlated
log entries; this is a diagnostic snapshot, not a complete durable log. Uncorrelated background logs and logs from
other tests are excluded. Propagate `traceparent` over the broker and create a consumer Activity as the example does.

Telemetry modules remain optional. Multiple OpenTelemetry providers in one process can observe the same global
instrumentation: application names alone do not isolate providers or guarantee correct span service attribution.
Use application-local logs and explicit business side effects as evidence; configure instrumentation deliberately.
An arbitrary background-service failure is not automatically a failed test: assert the observable result with a
bounded wait, and include application logging. Host liveness monitoring and separate-process hosting are future work.

Separate hosts still share the container data and the test process. Use unique business keys, explicit message
correlation and application-owned consumer queues. An observed or confirmed publication proves less than successful
worker processing; the example asserts both observation and the persisted result visible through HTTP.
