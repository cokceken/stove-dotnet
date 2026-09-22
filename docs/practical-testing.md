# Practical testing: HTTP, waiting, time and identity

These additions come from reviewing RailSense's HTTP response/upload helpers, waits, adjustable clock and OIDC
stub. RailSense was read as evidence and was not changed. Domain contracts, Keycloak roles, database seeders and
ingestion formats stay in the consumer project.

## HTTP assertions and custom requests

```csharp
var order = (await t.Http("api").Post<Order>("/orders", request))
    .Expect(HttpStatusCode.Created).Body;
(await t.Http("api").Delete("/orders/42")).Expect(HttpStatusCode.NoContent);

using var upload = new HttpRequestMessage(HttpMethod.Post, "/uploads")
{
    Content = new ByteArrayContent(compressedJson)
};
upload.Content.Headers.ContentEncoding.Add("gzip");
var response = (await t.Http("api").Send<UploadResult>(upload, t.CancellationToken))
    .Expect(HttpStatusCode.Accepted);
```

`Expect` returns the same response, retaining its generic type. It does not deserialize `Body`. Typed bodies remain
lazy; invalid JSON, JSON `null`, or a mismatched shape produce an error with the target type, JSON position and safe
response diagnostics. Existing constructors and `StatusCode`, `Headers`, `RawBody` and `IsSuccessStatusCode` remain.

`Send(HttpRequestMessage, CancellationToken)` and `Send<T>(...)` add correlation, buffer the response and dispose the
native response. The caller owns and disposes the request and its content. `SendRaw` remains available when the caller
wants the native response and its disposal responsibility. A new token overload lets raw calls participate in a polling
deadline. Tokens are linked with the current Stove test token. Default client headers now also apply to hand-built
requests when the request does not already contain that header; correlation headers always come from the Stove scope.

Status failures include method, URL, expected/actual status, headers and a bounded response-body preview. The default
preview is 2,048 characters. Common credential headers (`Authorization`, `Proxy-Authorization`, cookies, `X-Api-Key`,
`X-Device-Key`) are masked. URI user-info, fragments and query values are not printed. Configure additional policies:

```csharp
.WithHttpClient("api", o =>
{
    o.ApplicationName = "api";
    o.Diagnostics.MaxBodyLength = 512;
    o.Diagnostics.SensitiveHeaders.Add("X-Customer-Secret");
    o.Diagnostics.RedactUrl = _ => "[private route]";
    o.Diagnostics.RedactResponseBody = RedactPrivateJsonFields;
    // Optional; do not buffer binary or streaming uploads just to print them.
    o.Diagnostics.IncludeRequestBody = true;
    o.Diagnostics.RedactRequestBody = _ => "[request omitted]";
})
```

Redaction happens before truncation; redactors receive the complete body. A throwing redactor produces a safe
placeholder, not its exception or the original data. Request-body capture is off by default. These policies affect
diagnostic text only: wire data, response headers and `RawBody` remain unchanged. Body content is **not automatically
classified as sensitive**; configure a redactor or set `MaxBodyLength = 0` for secret-bearing APIs. `ToString()` and
deserialization errors now use bounded safe diagnostics instead of the complete raw body; deserialization errors omit
the original JSON exception because its text can contain secret values. Assertions still expose the original
`RawBody` explicitly when a caller needs it.

HTTP redaction does not sanitize application logs, telemetry, WireMock evidence or arbitrary assertion messages.
Configure those producers independently; do not log `RawBody` or tokens. Nor does preview truncation limit response
buffering: `SendRaw` retains HttpClient's default buffered completion too. Use an explicitly configured native HTTP
client with streaming completion for workloads where buffering a whole response is unsuitable.

Executable examples: [HTTP acceptance tests](../tests/StoveDotnet.Hosting.AcceptanceTests/HttpResponseTests.cs).

## Eventual assertions and effective cancellation

```csharp
await Eventually.AssertAsync(async ct =>
{
    using var request = new HttpRequestMessage(HttpMethod.Get, "/orders/42");
    (await t.Http("api").Send(request, ct)).Expect(HttpStatusCode.OK);
}, TimeSpan.FromSeconds(10), cancellationToken: t.CancellationToken);

var order = await Eventually.UntilAsync<Order?>(
    ct => ReadOrderFromDatabase(ct),
    value => value is not null,
    TimeSpan.FromSeconds(10),
    describeValue: value => value is null ? "missing" : "present",
    cancellationToken: t.CancellationToken);
```

The token-aware assertion and value-probe overloads receive the linked caller/deadline token. Forward it to actual
I/O. Their default retry predicate accepts only `StoveAssertionException`, including HTTP `Expect` failures.
Programming errors and cancellation propagate immediately. For framework assertions, explicitly select that
framework's assertion exception; avoid retrying all exceptions unless that is what your scenario needs:

```csharp
var retry = new EventuallyOptions
{
    PollInterval = TimeSpan.FromMilliseconds(100),
    RetryOn = error => error is StoveAssertionException or Xunit.Sdk.XunitException
};
await Eventually.AssertAsync(ct => AssertBusinessState(ct), TimeSpan.FromSeconds(10), retry, t.CancellationToken);
```

Compatibility rules:

| Overload | Existing/default retry behavior |
| --- | --- |
| `AssertAsync(Func<Task>, timeout, ct)` | Retains retries of every non-cancellation exception, including nested timeouts; 50–500 ms backoff. It cannot pass a deadline token to its delegate. |
| `UntilAsync(probe, timeout, description, signal?, ct)` | Retains retries of `false`, immediate exception propagation and optional change signals; 50–500 ms backoff. |
| New token-aware assertion, configurable bool probe and value probe | Retry only selected exceptions; fixed `PollInterval` (50 ms default). A nonmatching result is retried. |

Legacy zero-duration waits perform one immediate check (used by WireMock); legacy infinite waits still accept
`Timeout.InfiniteTimeSpan`. New overloads require a positive finite timeout. Timeout failures report attempts, elapsed
wall-clock time, the last observed value and/or last failure. The last retryable exception remains the inner exception.
Use `describeValue` to avoid printing sensitive objects. Caller cancellation propagates as cancellation, not a timeout.

Cancellation is **cooperative**. A probe that ignores its token can delay completion beyond the deadline. Stove awaits
that probe rather than abandoning it or starting overlapping work; a late success is rejected as a timeout for positive
finite deadlines. Application fake time does not advance these wall-clock polling deadlines.

```csharp
await Eventually.ThroughoutAsync(async ct =>
{
    using var request = new HttpRequestMessage(HttpMethod.Get, "/orders/absent-id");
    (await t.Http("api").Send(request, ct)).Expect(HttpStatusCode.NotFound);
}, TimeSpan.FromSeconds(1), cancellationToken: t.CancellationToken);
```

`ThroughoutAsync` samples sequentially until the duration expires. Any assertion failure propagates immediately.
After a successful sample, it starts another only when more than one polling interval remains; otherwise it waits
out the remaining duration. This avoids starting I/O at the deadline. There is no final boundary probe.
A probe still running when the deadline cancels is a timeout, not proof of absence. Success describes observations
at sample points; it cannot rule out changes between samples or after the interval. It is not a broker delivery guarantee.

## Opt-in application time

`StoveDotnet.Time` uses Microsoft's `FakeTimeProvider` from `Microsoft.Extensions.TimeProvider.Testing`; Stove adds
explicit DI registration and named application lookup, not a scheduler. See the
[Microsoft package documentation](https://www.nuget.org/packages/Microsoft.Extensions.TimeProvider.Testing/10.9.0).

```csharp
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Time.Testing;
using StoveDotnet.Time;

var clock = new FakeTimeProvider(new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero));
builder.WithAspNetCoreApplication<Program>("api", o =>
    o.ConfigureWebHost = web => web.ConfigureTestServices(s => s.UseStoveTime(clock)));

// In a worker factory, before Build(): host.Services.UseStoveTime(clock);
// Inside a Stove test:
t.Clock("api").Advance(TimeSpan.FromMinutes(5));
```

The fixture creates and owns the clock. `UseStoveTime` explicitly replaces `TimeProvider` registrations; it is never
enabled automatically. Pass the same instance to hosts that must share time, or create separate instances per host or
isolated fixture. No clock is silently reset between tests. Tests that mutate a shared clock must be serialized; tests
with independent hosts/clocks may run concurrently. See [serialized shared-clock tests](../tests/StoveDotnet.Time.AcceptanceTests/SharedClockTests.cs).

Advancement changes `GetUtcNow` and fires timers created through that provider, including provider-aware `Task.Delay`
and `PeriodicTimer`. Await the observable effect after advancing: asynchronous continuations are not necessarily done
when `Advance` returns, and periodic consumers can coalesce ticks. Ordinary `Task.Delay`, static UTC calls, PostgreSQL
`now()`, token validators and external services retain their own clocks. Explicitly injecting a clock into token issuance
does not synchronize a JWT validator. Moving the clock backwards is not a reset strategy; build another fixture instead.

[Time acceptance tests](../tests/StoveDotnet.Time.AcceptanceTests/TimeTests.cs) exercise real HTTP delays, hosted worker
timers, shared ownership, independent parallel hosts and timer cleanup.

## Optional OIDC module

`StoveDotnet.Oidc` serves discovery and JWKS over loopback HTTP with a fresh RSA signing key per system. It uses
Kestrel and Microsoft.IdentityModel; it does not depend on a test framework or carry Keycloak/RailSense role conventions.

```csharp
builder.WithOidc("identity", o =>
{
    o.Audience = "orders-api";
    o.ConfigureExposedConfiguration = c =>
    [
        new("Auth:Issuer", c.Issuer.ToString()),
        new("Auth:Metadata", c.MetadataAddress.ToString()),
        new("Auth:Audience", c.Audience),
        new("Auth:RequireHttpsMetadata", "false")
    ];
});

var token = t.Oidc("identity").IssueToken(o =>
{
    o.Claims["sub"] = "alice";
    o.Claims["permission"] = "write";
});
(await t.Http().Get("/me", new Dictionary<string, string> { ["Authorization"] = "Bearer " + token }))
    .Expect(HttpStatusCode.OK);
```

Your real application reads these keys into its JWT bearer configuration, as in the
[acceptance application](../tests/TestApps/OidcApp/Program.cs). Keep signature, issuer, audience and lifetime validation
enabled; only permit HTTP metadata in the test environment. No validation handler or authenticated principal is replaced.

The default issuer is the local server URL. A custom `OidcOptions.Issuer` changes discovery/token identity while discovery
and JWKS remain local: set the validator's `MetadataAddress` to the exposed local address. `Audience` is configurable.
Per-token options override issuer/audience, arbitrary claims, `IssuedAt`, `NotBefore`, `ExpiresAt` and signature mode
(`PublishedKey`, `InvalidSignature`, `UnknownKey`, `NoSignature`). Registered time/issuer/audience claims use the explicit
options rather than the claims dictionary. Tokens default to subject `stove-user` and a five-minute lifetime.

For expired tokens, put both issuance and expiration in the past; for future-valid tokens, set future issuance/not-before.
Token time defaults to `TimeProvider.System`; `OidcOptions.TimeProvider` explicitly changes issuance only. Validator clock
skew belongs to the application (the tests set it to zero). A missing header and malformed token can simply be sent by
the client. The [acceptance suite](../tests/StoveDotnet.Oidc.AcceptanceTests/OidcTests.cs) verifies 200/204, 401, 403,
custom issuers, HTTP metadata/key retrieval, independent named keys and cleanup through a real bearer pipeline.

This is a test token issuer, not a full identity provider: no login UI, authorization-code/refresh-token flow, key rotation,
revocation or user store. Only public key parameters are served; private keys and issued tokens are not diagnostic evidence.

## Choosing isolation

See the [runnable isolation examples](../examples/Isolation/README.md). Start with shared hosts and unique data when
business behavior is scoped by a key. Use independently owned environments for global state, destructive reset, schema
changes, exclusive consumers or clock-dependent behavior. A test-side database transaction cannot roll back writes made
over independent HTTP requests or background-worker connections.
