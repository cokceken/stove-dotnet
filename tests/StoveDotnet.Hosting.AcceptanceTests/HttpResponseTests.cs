using System.Net;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using StoveDotnet.Http;
using StoveDotnet.WireMock;
using Xunit;

namespace StoveDotnet.Hosting.AcceptanceTests;

public sealed class HttpResponseTests
{
    [Fact]
    public async Task String_enum_failure_preserves_serializer_details_through_stove_and_matching_options_succeed()
    {
        await using var stove = await StoveBuilder.Create().WithWireMock().WithHttpClient(o =>
            o.BaseAddress = new Uri("http://localhost")).StartAsync(TestContext.Current.CancellationToken);

        var failure = await Assert.ThrowsAsync<StoveTestFailedException>(() => stove.Test(async t =>
        {
            t.WireMock().MockRaw("GET", "/enum", 200, "{\"status\":\"Created\"}", "application/json");
            var response = (await t.Http().Get<EnumPayload>(new Uri(t.WireMock().ExposedConfiguration.BaseUrl, "/enum").ToString()))
                .Expect(HttpStatusCode.OK);
            _ = response.Body;
        }, TestContext.Current.CancellationToken));

        var error = Assert.IsType<InvalidOperationException>(failure.InnerException);
        var original = Assert.IsType<JsonException>(error.InnerException);
        Assert.Equal("$.status", original.Path);
        Assert.Contains(original.Message, error.Message, StringComparison.Ordinal);
        Assert.Contains("$.status", error.Message, StringComparison.Ordinal);
        Assert.Contains("GET", error.Message, StringComparison.Ordinal);
        Assert.Contains("/enum", error.Message, StringComparison.Ordinal);
        Assert.Contains("200 OK", error.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(JsonSerializer), original.StackTrace!, StringComparison.Ordinal);

        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());
        var typed = new StoveHttpResponse<EnumPayload>(
            new StoveHttpResponse(HttpStatusCode.OK, new Dictionary<string, IReadOnlyList<string>>(), "{\"status\":\"Created\"}"), options);
        Assert.Equal(OrderStatus.Created, typed.Expect(HttpStatusCode.OK).Body.Status);
    }

    [Fact]
    public void Custom_converter_json_exception_is_preserved_without_losing_its_cause()
    {
        var cause = new InvalidOperationException("Converter configuration is missing.");
        var original = new JsonException("Custom converter failed.", cause);
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new FailingPayloadConverter(original));
        var response = new StoveHttpResponse<Payload>(
            new StoveHttpResponse(HttpStatusCode.OK, new Dictionary<string, IReadOnlyList<string>>(), "{}"), options);

        var error = Assert.Throws<InvalidOperationException>(() => response.Body);
        Assert.Same(original, error.InnerException);
        Assert.Same(cause, original.InnerException);
        Assert.Contains(original.Message, error.Message, StringComparison.Ordinal);
        Assert.Same(error, Assert.Throws<InvalidOperationException>(() => response.Body));
    }

    [Fact]
    public async Task Status_assertions_are_fluent_and_malformed_json_is_lazy_and_redacted()
    {
        await using var stove = await StoveBuilder.Create().WithWireMock().WithHttpClient(o =>
        {
            o.BaseAddress = new Uri("http://localhost");
            o.Diagnostics.MaxBodyLength = 80;
            o.Diagnostics.RedactResponseBody = body => body.Replace("body-secret", "[redacted]", StringComparison.Ordinal);
        }).StartAsync(TestContext.Current.CancellationToken);
        await stove.Test(async t =>
        {
            var server = t.WireMock();
            server.Server.Given(global::WireMock.RequestBuilders.Request.Create().WithPath("/bad").UsingGet())
                .RespondWith(global::WireMock.ResponseBuilders.Response.Create().WithStatusCode(400).WithBody("body-secret " + new string('x', 200)));
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(server.ExposedConfiguration.BaseUrl, "/bad?token=query-secret"));
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer header-secret");
            request.Headers.TryAddWithoutValidation("X-Device-Key", "device-secret");
            var response = await t.Http().Send<Payload>(request);
            Assert.Same(response, response.Expect(HttpStatusCode.BadRequest));
            var error = Assert.Throws<StoveAssertionException>(() => response.Expect(HttpStatusCode.OK));
            Assert.Contains("GET", error.Message, StringComparison.Ordinal);
            Assert.Contains("/bad", error.Message, StringComparison.Ordinal);
            Assert.Contains("200 OK", error.Message, StringComparison.Ordinal);
            Assert.Contains("400 BadRequest", error.Message, StringComparison.Ordinal);
            Assert.Contains("[truncated]", error.Message, StringComparison.Ordinal);
            foreach (var secret in new[] { "body-secret", "query-secret", "header-secret", "device-secret" })
                Assert.DoesNotContain(secret, error.ToString(), StringComparison.Ordinal);
            Assert.Contains("body-secret", response.RawBody, StringComparison.Ordinal); // Actual response stays intact.
            var malformed = Assert.Throws<InvalidOperationException>(() => response.Body);
            Assert.Contains("Payload", malformed.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("body-secret", malformed.ToString(), StringComparison.Ordinal);
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Null_empty_and_incompatible_bodies_fail_only_on_typed_access()
    {
        await using var stove = await StoveBuilder.Create().WithWireMock()
            .WithHttpClient(o => o.BaseAddress = new Uri("http://localhost")).StartAsync(TestContext.Current.CancellationToken);
        await stove.Test(async t =>
        {
            foreach (var body in new[] { "null", "", "[]" })
            {
                t.WireMock().MockRaw("GET", "/unexpected", 200, body, "application/json");
                var response = (await t.Http().Get<Payload>(new Uri(t.WireMock().ExposedConfiguration.BaseUrl, "/unexpected").ToString()))
                    .Expect(HttpStatusCode.OK);
                Assert.Equal(body, response.RawBody);
                var error = Assert.Throws<InvalidOperationException>(() => response.Body);
                Assert.Contains("Payload", error.Message, StringComparison.Ordinal);
                Assert.Contains("/unexpected", error.Message, StringComparison.Ordinal);
            }
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Custom_gzip_request_preserves_content_correlation_and_raw_access()
    {
        await using var stove = await StoveBuilder.Create().WithWireMock().WithHttpClient(o => o.BaseAddress = new Uri("http://localhost")).StartAsync(TestContext.Current.CancellationToken);
        await stove.Test(async t =>
        {
            var server = t.WireMock();
            server.MockPost("/upload", responseBody: new { value = "accepted" });
            using var buffer = new MemoryStream();
            await using (var gzip = new GZipStream(buffer, CompressionMode.Compress, leaveOpen: true))
                await gzip.WriteAsync(Encoding.UTF8.GetBytes("{\"device\":1}"), t.CancellationToken);
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(server.ExposedConfiguration.BaseUrl, "/upload")) { Content = new ByteArrayContent(buffer.ToArray()) };
            request.Content.Headers.ContentEncoding.Add("gzip");
            var response = await t.Http().Send<Payload>(request);
            Assert.Equal("accepted", response.Expect(HttpStatusCode.OK).Body.Value);
            Assert.Equal(t.TestId, request.Headers.GetValues(StoveHeaders.TestId).Single());
            var received = Assert.Single(await server.ShouldHaveBeenCalled("POST", "/upload"));
            Assert.Equal(t.TestId, received.Header(StoveHeaders.TestId));
            Assert.Equal("gzip", received.Header("Content-Encoding"));
            Assert.Equal(buffer.ToArray(), await request.Content.ReadAsByteArrayAsync(t.CancellationToken));
            Assert.Same(response, ((StoveHttpResponse)response).Expect(HttpStatusCode.OK));
            using var rawRequest = new HttpRequestMessage(HttpMethod.Post, new Uri(server.ExposedConfiguration.BaseUrl, "/upload"));
            using var raw = await t.Http().SendRaw(rawRequest);
            Assert.Equal(HttpStatusCode.OK, raw.StatusCode);
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Request_and_response_redaction_fail_closed_including_cookies()
    {
        await using var stove = await StoveBuilder.Create().WithWireMock().WithHttpClient(o =>
        {
            o.BaseAddress = new Uri("http://localhost");
            o.Diagnostics.IncludeRequestBody = true;
            o.Diagnostics.RedactRequestBody = _ => throw new InvalidOperationException("request-secret");
            o.Diagnostics.RedactResponseBody = _ => "safe-response";
            o.Diagnostics.RedactUrl = _ => "hidden-path";
            o.Diagnostics.SensitiveHeaders.Add("X-Private");
        }).StartAsync(TestContext.Current.CancellationToken);
        await stove.Test(async t =>
        {
            t.WireMock().Server.Given(global::WireMock.RequestBuilders.Request.Create().WithPath("/private").UsingPost())
                .RespondWith(global::WireMock.ResponseBuilders.Response.Create().WithStatusCode(403).WithHeader("Set-Cookie", "cookie-secret").WithBody("response-secret"));
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(t.WireMock().ExposedConfiguration.BaseUrl, "/private")) { Content = new StringContent("request-secret") };
            request.Headers.TryAddWithoutValidation("X-Private", "header-secret");
            var response = await t.Http().Send(request);
            var error = Assert.Throws<StoveAssertionException>(() => response.Expect(HttpStatusCode.OK));
            Assert.Contains("[redaction failed]", error.Message, StringComparison.Ordinal);
            foreach (var secret in new[] { "cookie-secret", "response-secret", "request-secret", "header-secret", "/private" })
                Assert.DoesNotContain(secret, error.ToString(), StringComparison.Ordinal);
        }, TestContext.Current.CancellationToken);
    }

    private sealed record Payload(string Value);
    private enum OrderStatus { Created }
    private sealed record EnumPayload(OrderStatus Status);
    private sealed class FailingPayloadConverter(JsonException exception) : JsonConverter<Payload>
    {
        public override Payload? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => throw exception;
        public override void Write(Utf8JsonWriter writer, Payload value, JsonSerializerOptions options) => throw new NotSupportedException();
    }
}
