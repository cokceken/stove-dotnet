# Typed WireMock fakes from API definitions

Third-party HTTP dependencies are faked with **one typed fake class per API**, wrapping a named WireMock instance.
Tests read like scenarios (`t.PaymentsFake().ChargeDeclined()`) and verify outgoing calls with typed bodies
(`await payments.ShouldHaveCharged(c => c.Amount == 20m)`).

The source of truth is the API definition: an OpenAPI/Swagger document, the spec behind a generated client (NSwag,
Kiota, Refit, AutoRest), an SDK's service model, or the provider's documentation.

## Workflow

1. **Find the definition.** Look for `*.yaml`/`*.json` specs in the repo (`openapi`, `swagger`, `specs/`), NSwag/Kiota
   config files (`nswag.json`, `kiota-lock.json`, which record the spec URL), Refit interfaces, or ask the user for the
   spec file. For SDKs see [SDKs and non-OpenAPI definitions](#sdks-and-non-openapi-definitions).
2. **Find the operations the app actually calls.** Search the app for the generated client, `HttpClient` usage or SDK
   calls. Fake only those operations. A 300-operation spec does not need a 300-method fake.
3. **Find how the app is pointed at the API.** Usually a base URL configuration key (`Payments:BaseUrl`,
   `PaymentsClientOptions.BaseAddress`, `ServiceURL`). If it is hard-coded, propose making it configurable; that is
   production-useful anyway.
4. **Model the wire types.** For each operation's request body and each response you stub, write `record`s from
   `components/schemas`:
   - Prefix names with the API (`PaymentsChargeRequest`) so they don't clash with app types or other fakes' records.
   - Map types as in [Schema to C# types](#schema-to-c-types).
   - Property names follow the JSON names through System.Text.Json web defaults (camelCase). If the spec uses
     snake_case or other names, add `[JsonPropertyName("...")]` (`using System.Text.Json.Serialization;`).
   - Do not reuse the generated client's DTOs. Separate wire records make the tests catch client or serialization
     contract drift.
5. **Write scenario methods**, named after the business outcome:
   - Write one for **every documented response** of each called operation:
     - 2xx: `ChargeSucceeds(paymentId)`, `StockAvailable(productId, available)`
     - 4xx: `ChargeDeclined(code, message)`, `ProductUnknown(productId)`
     - documented 5xx: `PaymentsUnavailable()`
   - Add **undocumented** failures (a generic 500, `ChargeTimesOut()` with a `delay:` longer than the client timeout)
     only when the app handles them (retries, circuit breakers, fallbacks) or the user asks for them.
   - Parameters are the values tests care about. Identifiers the stub path depends on (`productId`) are required
     parameters. Other values default to the spec's `example`; when there is none, use a short realistic value
     (`"product not found"`).
   - Return `this` for chaining.
6. **Write verification methods**, one `ShouldHave<BusinessVerbPastTense>` per called operation (`createCharge` →
   `ShouldHaveCharged`, `getStock` → `ShouldHaveCheckedStock`), plus `ShouldNot…`:
   - Operations with a request body return the typed body; the template shows this.
   - Operations without a body take the identifying path values as parameters and verify that path (see
     [Verifying bodiless operations](#verifying-bodiless-operations)).
   - Expose `times = 1` when the app can legitimately call the operation more than once in a test. Methods that return
     a single body keep the exact count of 1; tests that expect several calls use `fake.WireMock.Requests(...)`.
   - Expose `within` so tests can wait for calls the app makes in the background.
7. **Write the registration extensions** (`WithXxxFake`, `t.XxxFake()`), then use them in the fixture and tests.
   Register each fake once per fixture. Base URL configuration keys must be unique across all systems.
8. **Check scenarios against the app's behavior.** Read how the app handles each response. For example,
   `GetFromJsonAsync` throws on 404, so `ProductUnknown` makes the app return 500. Name scenarios after what the
   *provider* does, not what the app does. Point out app behavior that looks unintended to the user instead of hiding
   it in the fake.
9. **Build and run** the tests. Read any `UNMATCHED` entries in the failure output (see `diagnosing-failures.md`).

## WireMockSystem API used by fakes

| Member | Notes |
|---|---|
| `Guid MockGet(path, statusCode = 200, responseBody = null, responseHeaders = null, delay = null)` | also `MockDelete` with the same shape |
| `Guid MockPost(path, statusCode = 200, responseBody = null, requestBody = null, responseHeaders = null, delay = null)` | also `MockPut`, `MockPatch`; `requestBody` is a JSON equality match |
| `Guid MockRaw(method, path, statusCode, string body, contentType, responseHeaders = null, delay = null)` | also a `byte[] body` overload; for XML, text or binary |
| `Guid Stub(Func<IRequestBuilder, IRequestBuilder> request, Func<IResponseBuilder, IResponseBuilder> response)` | WireMock.Net builders; use `request.WithPathTemplate("/items/{id}")` |
| `IReadOnlyList<RecordedRequest> Requests(method = null, path = null)` | the current test's requests; no waiting, no count check |
| `Task<IReadOnlyList<RecordedRequest>> ShouldHaveBeenCalled(method, path, times = 1, within = null)` | exact count; returns the matching requests |
| `Task ShouldNotHaveBeenCalled(method, path)` | same as `times: 0` |
| `RecordedRequest` | `Method, Path, Url, PathParameters, Query, Headers, Body, BodyBytes`, `BodyAs<T>()`, `Header(name)`, `QueryValue(name)` |
| `WireMockExposedConfiguration ExposedConfiguration` | `BaseUrl (Uri), Host, Port` |
| `WireMockServer Server` | raw WireMock.Net server; last resort |

Rules for `path`, everywhere:

- It may be an exact path or an OpenAPI template: `{name}` matches one segment and `{name+}` matches the rest,
  including slashes.
- Matching ignores case.
- The newest matching stub wins, so a test can override a scenario by calling another scenario method.
- `responseBody` objects are serialized as JSON (camelCase); `string` bodies are sent as-is.

## Schema to C# types

| Schema | C# |
|---|---|
| `string` | `string` |
| `string`, `format: uuid` | `Guid` |
| `string`, `format: date-time` / `date` | `DateTimeOffset` / `DateOnly` |
| `string` with `enum` | `string`, unless the app switches on specific values |
| `integer` (`int32` or no format) / `int64` | `int` / `long` |
| `number` for money or `format: decimal` / other `number` | `decimal` / `double` |
| `boolean` | `bool` |
| `array` of X | `IReadOnlyList<X>` |
| `object` with `properties` | a nested `record` |
| `object` with only `additionalProperties` | `IReadOnlyDictionary<string, X>` |
| not in `required`, or `nullable: true` / `type: [X, "null"]` | nullable (`X?`) |

Constraints (`minimum`, `pattern`, `maxLength`) are not enforced in records. Use them to pick realistic default values.

## Template

This mirrors `examples/OrderService.E2ETests.XunitV3/Fakes/PaymentsFake.cs` in the stove-dotnet repository, which is
generated from `examples/OrderService/specs/payments-api.yaml` and runs in CI.

```csharp
using StoveDotnet;
using StoveDotnet.WireMock;

namespace MyService.E2ETests.Fakes;

/// <summary>
/// Fake of the Payments API, generated from specs/payments-api.yaml.
/// Covers only the operations MyService calls: createCharge.
/// </summary>
public sealed class PaymentsFake(WireMockSystem wireMock)
{
    public const string InstanceName = "payments";

    /// <summary>The underlying WireMock instance, for scenarios this fake does not cover.</summary>
    public WireMockSystem WireMock { get; } = wireMock;

    // createCharge: POST /charges

    /// <summary>200: the charge succeeds with <paramref name="paymentId"/>.</summary>
    public PaymentsFake ChargeSucceeds(string paymentId = "pay_8f2c1")
    {
        WireMock.MockPost("/charges", statusCode: 200, responseBody: new PaymentsCharge(paymentId));
        return this;
    }

    /// <summary>402: the charge is declined.</summary>
    public PaymentsFake ChargeDeclined(string code = "card_declined", string message = "The card was declined.")
    {
        WireMock.MockPost("/charges", statusCode: 402, responseBody: new PaymentsError(code, message));
        return this;
    }

    /// <summary>503: the provider is unavailable.</summary>
    public PaymentsFake PaymentsUnavailable()
    {
        WireMock.MockPost("/charges", statusCode: 503);
        return this;
    }

    /// <summary>Asserts exactly one charge was requested (optionally matching) and returns its body.</summary>
    public async Task<PaymentsChargeRequest> ShouldHaveCharged(Func<PaymentsChargeRequest, bool>? matching = null, TimeSpan? within = null)
    {
        // ShouldHaveBeenCalled asserts the exact count (1 by default), so the first request is the one.
        var requests = await WireMock.ShouldHaveBeenCalled("POST", "/charges", within: within);
        var charge = requests[0].BodyAs<PaymentsChargeRequest>();
        if (matching is not null && !matching(charge))
        {
            throw new StoveAssertionException($"{InstanceName}: a charge was requested but it did not match the expectation: {charge}");
        }

        return charge;
    }

    public async Task ShouldNotHaveCharged() =>
        await WireMock.ShouldNotHaveBeenCalled("POST", "/charges");
}

// Wire models from components/schemas. Kept separate from the application's own types so the tests check the contract.
public sealed record PaymentsChargeRequest(Guid OrderId, string CustomerId, decimal Amount);

public sealed record PaymentsCharge(string PaymentId);

public sealed record PaymentsError(string Code, string Message);

public static class PaymentsFakeExtensions
{
    /// <summary>Registers the fake and points the application's Payments client at it.</summary>
    public static StoveBuilder WithPaymentsFake(this StoveBuilder builder, string baseUrlKey = "Payments:BaseUrl") =>
        builder.WithWireMock(Fakes.PaymentsFake.InstanceName, o =>
            o.ConfigureExposedConfiguration = c => [new(baseUrlKey, c.BaseUrl.ToString())]);

    public static PaymentsFake PaymentsFake(this StoveTestContext test) =>
        new(test.WireMock(Fakes.PaymentsFake.InstanceName));
}
```

Usage:

```csharp
// fixture
StoveBuilder.Create().WithPaymentsFake().WithInventoryFake() /* ... */ .WithAspNetCoreApplication<Program>();

// test
var payments = t.PaymentsFake().ChargeSucceeds(paymentId: "pay-1");
var response = await t.Http().Post<Order>("/orders", request);
var charge = await payments.ShouldHaveCharged(c => c.OrderId == response.Body.Id);
Assert.Equal(20m, charge.Amount);
```

### Verifying bodiless operations

This mirrors `InventoryFake.cs` in the example project:

```csharp
// getStock: GET /stock/{productId}

/// <summary>200: the product has <paramref name="available"/> units in stock.</summary>
public InventoryFake StockAvailable(string productId, int available)
{
    WireMock.MockGet($"/stock/{productId}", statusCode: 200, responseBody: new InventoryStock(available));
    return this;
}

/// <summary>Asserts the stock of <paramref name="productId"/> was checked <paramref name="times"/> time(s).</summary>
public async Task ShouldHaveCheckedStock(string productId, int times = 1, TimeSpan? within = null) =>
    await WireMock.ShouldHaveBeenCalled("GET", $"/stock/{productId}", times, within);

/// <summary>Asserts no stock was checked for any product.</summary>
public async Task ShouldNotHaveCheckedStock() =>
    await WireMock.ShouldNotHaveBeenCalled("GET", "/stock/{productId}");
```

Stubs use the concrete path, because the test knows the id. "Not called at all" checks use the template, so they cover
every id.

Conventions:

- **Type references inside the extensions class.** Inside `PaymentsFakeExtensions`, the simple name `PaymentsFake`
  refers to the accessor *method* `PaymentsFake(this StoveTestContext)`, not the type. Qualify the type wherever a
  value is needed: `Fakes.PaymentsFake.InstanceName`, where `Fakes` is the last segment of the fake's namespace, or
  `global::MyService.E2ETests.Fakes.PaymentsFake`. Return types and `new(...)` do not need the qualifier.
- **Doc comment.** Name the spec path relative to the repository root and list the operations the fake covers.
- **Usings.** The template relies on the SDK's implicit usings (`<ImplicitUsings>enable</ImplicitUsings>`, the default
  for new projects). Add `System`, `System.Collections.Generic` and `System.Threading.Tasks` usings if they are disabled.
- **Stub lifetime.** Fakes hold no state and are created per call to `t.XxxFake()`. Stubs belong to the running test and
  are removed when it ends.
- **Paths with parameters.** Stub the concrete path when the test knows the value (`$"/stock/{productId}"`). Use the
  spec template (`"/stock/{productId}"`) when any value should match, and read `requests[0].PathParameters["productId"]`.
- **Query, headers or partial bodies.** When a scenario depends on them, use
  `WireMock.Stub(req => req.WithPathTemplate(...).UsingGet().WithParam("page", "2").WithHeader("Authorization", "Bearer *"), res => ...)`
  and return the stub from the scenario method.
- **Auth.** Stub token endpoints (for example `POST /oauth/token`) in a scenario like `Authenticates()`. Call it inside
  the other scenarios, or have the fixture's tests call it first.
- **Async calls.** For calls the app makes in the background, expose `within` on verification methods.
- **Where fakes live.** `Fakes/<Api>Fake.cs` in the e2e test project, with a doc comment naming the spec file and the
  operations covered. Keep the spec in the repo next to the app (for example `specs/`).

## SDKs and non-OpenAPI definitions

When the app uses a vendor SDK, the SDK is still an HTTP client. Fake the HTTP API behind it:

1. **Find the endpoint override** in the SDK config and bind it to configuration in the app:
   - AWS: `ServiceURL` plus `ForcePathStyle = true` (S3). Use dummy credentials in tests.
   - Azure SDKs: the account or endpoint URI.
   - Stripe: `StripeClient(apiBase: ...)`.
   - Google APIs: `BaseUri`.
2. **Find the wire contract:**
   - AWS publishes Smithy models (github.com/aws/api-models-aws); their `http` traits give method and URI (`/{Bucket}/{Key+}`)
     and their shapes give the XML members.
   - Many vendors publish OpenAPI (Stripe, GitHub, Twilio).
   - Otherwise use the vendor's REST documentation.
3. **Match the response format the SDK parses.** Use `MockRaw` for XML or binary, and include headers the SDK reads
   (`ETag`, `Content-Length`, request ids).
4. **Keep only what the app calls.**

### Walkthrough: S3 upload and download with AWSSDK.S3

App configuration (production defaults, overridable in tests):

```csharp
builder.Services.AddSingleton<IAmazonS3>(_ => new AmazonS3Client(
    new BasicAWSCredentials(configuration["Storage:AccessKey"], configuration["Storage:SecretKey"]),
    new AmazonS3Config { ServiceURL = configuration["Storage:ServiceUrl"], ForcePathStyle = true }));
```

The fake is built from the Smithy `PutObject` (`PUT /{Bucket}/{Key+}`) and `GetObject` (`GET /{Bucket}/{Key+}`)
operations:

```csharp
public sealed class S3Fake(WireMockSystem wireMock)
{
    public const string InstanceName = "s3";
    public WireMockSystem WireMock { get; } = wireMock;

    // PutObject: PUT /{Bucket}/{Key+}
    public S3Fake PutObjectSucceeds(string bucket, string key, string etag = "\"9b2cf535f27731c974343645a3985328\"")
    {
        WireMock.MockRaw("PUT", $"/{bucket}/{key}", 200, string.Empty, "application/xml",
            new Dictionary<string, string> { ["ETag"] = etag });
        return this;
    }

    // GetObject: GET /{Bucket}/{Key+}
    public S3Fake ObjectExists(string bucket, string key, byte[] content, string contentType = "application/octet-stream")
    {
        WireMock.MockRaw("GET", $"/{bucket}/{key}", 200, content, contentType,
            new Dictionary<string, string> { ["ETag"] = "\"9b2cf535f27731c974343645a3985328\"" });
        return this;
    }

    public S3Fake ObjectMissing(string bucket, string key)
    {
        WireMock.MockRaw("GET", $"/{bucket}/{key}", 404,
            $"<?xml version=\"1.0\" encoding=\"UTF-8\"?><Error><Code>NoSuchKey</Code><Message>The specified key does not exist.</Message><Key>{key}</Key></Error>",
            "application/xml");
        return this;
    }

    /// <summary>Asserts one upload to <paramref name="bucket"/> and returns the uploaded key and bytes.</summary>
    public async Task<(string Key, byte[] Content)> ShouldHaveUploaded(string bucket, TimeSpan? within = null)
    {
        var requests = await WireMock.ShouldHaveBeenCalled("PUT", $"/{bucket}/{{key+}}", within: within);
        return (requests[0].PathParameters["key"], requests[0].BodyBytes ?? []);
    }
}

public static class S3FakeExtensions
{
    public static StoveBuilder WithS3Fake(this StoveBuilder builder) =>
        builder.WithWireMock(Fakes.S3Fake.InstanceName, o => o.ConfigureExposedConfiguration = c =>
        [
            new("Storage:ServiceUrl", c.BaseUrl.ToString()),
            new("Storage:AccessKey", "test"),
            new("Storage:SecretKey", "test"),
        ]);

    public static S3Fake S3Fake(this StoveTestContext test) => new(test.WireMock(Fakes.S3Fake.InstanceName));
}
```

S3 notes:

- **Signatures.** The SDK signs requests; WireMock does not validate signatures.
- **Chunked uploads.** The SDK may send `aws-chunked` bodies. If `BodyBytes` contains chunk headers, disable chunked
  encoding in the app's put request (`UseChunkEncoding = false`), or assert on the key and length only.
- **Presigned URLs.** When the app returns presigned or direct URLs for downloads, assert on the URL shape. The download
  itself happens outside the app.
