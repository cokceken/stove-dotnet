using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using WireMock;
using WireMock.Matchers;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;
using WireMock.Settings;

namespace StoveDotnet.WireMock;

public sealed record WireMockExposedConfiguration(Uri BaseUrl, string Host, int Port) : IExposedConfiguration;

public sealed class WireMockOptions : SystemOptions<WireMockExposedConfiguration>
{
    /// <summary>Port to listen on; 0 picks a free port.</summary>
    public int Port { get; set; }

    /// <summary>Serializer used for request-body matching and JSON response bodies.</summary>
    public JsonSerializerOptions JsonSerializerOptions { get; set; } = new(JsonSerializerDefaults.Web);

    /// <summary>By default, stubs created inside a test are removed when the test ends.</summary>
    public bool KeepStubsAfterTest { get; set; }

    /// <summary>
    /// When true, stubs created inside a test only match requests that continue the test's trace (W3C
    /// <c>traceparent</c>). Lets parallel tests stub the same endpoint differently; requires the application to
    /// propagate trace context on outgoing HTTP calls.
    /// </summary>
    public bool ScopeStubsToTest { get; set; }

    /// <summary>Additional WireMock.Net server settings.</summary>
    public Action<WireMockServerSettings>? ConfigureSettings { get; set; }
}

/// <summary>An in-process WireMock.Net server standing in for a third-party HTTP dependency.</summary>
public sealed class WireMockSystem
    : ExposingSystem<WireMockOptions, WireMockExposedConfiguration>, IRunAware, ITestScopeAware, IFailureDetailsProvider
{
    private readonly ConcurrentDictionary<string, ConcurrentBag<Guid>> _stubsByTest = new(StringComparer.Ordinal);
    private WireMockServer? _server;

    public WireMockSystem(string? name, WireMockOptions options)
        : base(name, options)
    {
    }

    /// <summary>The underlying WireMock.Net server for anything the Stove DSL does not cover.</summary>
    public WireMockServer Server => _server ?? throw new InvalidOperationException($"{DisplayName} is not running yet.");

    public Task RunAsync(CancellationToken cancellationToken)
    {
        var settings = new WireMockServerSettings { Port = Options.Port, StartAdminInterface = false };
        Options.ConfigureSettings?.Invoke(settings);
        _server = WireMockServer.Start(settings);

        var baseUrl = new Uri(_server.Url!);
        Expose(new WireMockExposedConfiguration(baseUrl, baseUrl.Host, _server.Port));
        return Task.CompletedTask;
    }

    public Guid MockGet(string path, int statusCode = 200, object? responseBody = null,
        IDictionary<string, string>? responseHeaders = null, TimeSpan? delay = null) =>
        Mock("GET", path, statusCode, requestBody: null, responseBody, responseHeaders, delay);

    public Guid MockPost(string path, int statusCode = 200, object? responseBody = null, object? requestBody = null,
        IDictionary<string, string>? responseHeaders = null, TimeSpan? delay = null) =>
        Mock("POST", path, statusCode, requestBody, responseBody, responseHeaders, delay);

    public Guid MockPut(string path, int statusCode = 200, object? responseBody = null, object? requestBody = null,
        IDictionary<string, string>? responseHeaders = null, TimeSpan? delay = null) =>
        Mock("PUT", path, statusCode, requestBody, responseBody, responseHeaders, delay);

    public Guid MockPatch(string path, int statusCode = 200, object? responseBody = null, object? requestBody = null,
        IDictionary<string, string>? responseHeaders = null, TimeSpan? delay = null) =>
        Mock("PATCH", path, statusCode, requestBody, responseBody, responseHeaders, delay);

    public Guid MockDelete(string path, int statusCode = 200, object? responseBody = null,
        IDictionary<string, string>? responseHeaders = null, TimeSpan? delay = null) =>
        Mock("DELETE", path, statusCode, requestBody: null, responseBody, responseHeaders, delay);

    /// <summary>Creates a stub with WireMock.Net's own builders, e.g. <c>Stub(r => r.WithPath("/x").UsingGet(), r => r.WithStatusCode(204))</c>.</summary>
    public Guid Stub(Func<IRequestBuilder, IRequestBuilder> request, Func<IResponseBuilder, IResponseBuilder> response)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(response);
        var test = StoveTestContext.Require();
        var requestBuilder = request(Request.Create());
        if (Options.ScopeStubsToTest)
        {
            requestBuilder = requestBuilder.WithHeader(
                StoveHeaders.Traceparent, $"00-{test.TraceId}-*", ignoreCase: true, MatchBehaviour.AcceptOnMatch);
        }

        var guid = Guid.NewGuid();
        Server.Given(requestBuilder).WithGuid(guid).RespondWith(response(Response.Create()));
        _stubsByTest.GetOrAdd(test.TestId, _ => []).Add(guid);
        return guid;
    }

    /// <summary>
    /// Stubs a non-JSON text response, e.g. XML for S3-style APIs. <paramref name="path"/> may be a template such as
    /// <c>/{bucket}/{key+}</c>.
    /// </summary>
    public Guid MockRaw(string method, string path, int statusCode, string body, string contentType,
        IDictionary<string, string>? responseHeaders = null, TimeSpan? delay = null) =>
        Stub(
            request => request.WithPathTemplate(path).UsingMethod(method),
            response => WithHeadersAndDelay(response.WithStatusCode(statusCode).WithHeader("Content-Type", contentType).WithBody(body), responseHeaders, delay));

    /// <summary>Stubs a binary response, e.g. a file download.</summary>
    public Guid MockRaw(string method, string path, int statusCode, byte[] body, string contentType,
        IDictionary<string, string>? responseHeaders = null, TimeSpan? delay = null) =>
        Stub(
            request => request.WithPathTemplate(path).UsingMethod(method),
            response => WithHeadersAndDelay(response.WithStatusCode(statusCode).WithHeader("Content-Type", contentType).WithBody(body), responseHeaders, delay));

    /// <summary>
    /// Requests the current test sent to this instance, optionally filtered by method and by a path or path template
    /// (e.g. <c>/stock/{productId}</c>). Path parameters are extracted from the template.
    /// </summary>
    public IReadOnlyList<RecordedRequest> Requests(string? method = null, string? path = null)
    {
        var test = StoveTestContext.Require();
        var template = path is null ? null : PathTemplate.Parse(path);
        var requests = new List<RecordedRequest>();
        foreach (var message in RequestsOf(test))
        {
            if (method is not null && !string.Equals(message.Method, method, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            IReadOnlyDictionary<string, string> parameters = new Dictionary<string, string>();
            if (template is not null && !template.TryMatch(message.Path, out parameters))
            {
                continue;
            }

            requests.Add(new RecordedRequest(message, parameters, Options.JsonSerializerOptions));
        }

        return requests;
    }

    /// <summary>Raw WireMock.Net request messages of the current test; prefer <see cref="Requests"/>.</summary>
    public IReadOnlyList<IRequestMessage> CallsFor(string? method, string path)
    {
        var template = PathTemplate.Parse(path);
        return RequestsOf(StoveTestContext.Require())
            .Where(r => template.IsMatch(r.Path))
            .Where(r => method is null || string.Equals(r.Method, method, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    /// <summary>
    /// Asserts the current test sent exactly <paramref name="times"/> requests matching <paramref name="method"/> and
    /// <paramref name="path"/> (a path or template) and returns them, e.g. to assert on their bodies. For calls made
    /// asynchronously by the application, pass <paramref name="within"/> to wait for them.
    /// </summary>
    public async Task<IReadOnlyList<RecordedRequest>> ShouldHaveBeenCalled(string method, string path, int times = 1, TimeSpan? within = null)
    {
        var test = StoveTestContext.Require();
        try
        {
            await Eventually.UntilAsync(
                _ => ValueTask.FromResult(Requests(method, path).Count >= times),
                within ?? TimeSpan.Zero,
                () => string.Empty,
                cancellationToken: test.CancellationToken).ConfigureAwait(false);
        }
        catch (StoveTimeoutException)
        {
            // Reported below with the actual count.
        }

        var requests = Requests(method, path);
        if (requests.Count != times)
        {
            throw new StoveAssertionException(
                $"{DisplayName}: expected {method} {path} to be called {times} time(s) but it was called {requests.Count} time(s).{Environment.NewLine}{DescribeRequests(test)}");
        }

        return requests;
    }

    public async Task ShouldNotHaveBeenCalled(string method, string path) =>
        await ShouldHaveBeenCalled(method, path, times: 0).ConfigureAwait(false);

    public Task OnTestStartedAsync(StoveTestContext test) => Task.CompletedTask;

    public Task OnTestEndedAsync(StoveTestContext test, Exception? failure)
    {
        ArgumentNullException.ThrowIfNull(test);
        if (!Options.KeepStubsAfterTest && _server is not null && _stubsByTest.TryRemove(test.TestId, out var stubs))
        {
            foreach (var guid in stubs)
            {
                _server.DeleteMapping(guid);
            }
        }

        return Task.CompletedTask;
    }

    public Task<FailureDetails?> DescribeAsync(StoveTestContext test, CancellationToken cancellationToken) =>
        Task.FromResult<FailureDetails?>(new FailureDetails(
            Name is null ? "wiremock" : $"wiremock '{Name}'",
            DescribeRequests(test)));

    private Guid Mock(string method, string path, int statusCode, object? requestBody, object? responseBody,
        IDictionary<string, string>? responseHeaders, TimeSpan? delay) =>
        Stub(
            request =>
            {
                var builder = request.WithPathTemplate(path).UsingMethod(method);
                return requestBody is null
                    ? builder
                    : builder.WithBody(new JsonMatcher(Serialize(requestBody), ignoreCase: false));
            },
            response =>
            {
                var builder = response.WithStatusCode(statusCode);
                if (responseBody is not null)
                {
                    builder = responseBody is string text
                        ? builder.WithBody(text)
                        : builder.WithHeader("Content-Type", "application/json").WithBody(Serialize(responseBody));
                }

                return WithHeadersAndDelay(builder, responseHeaders, delay);
            });

    private static IResponseBuilder WithHeadersAndDelay(IResponseBuilder builder, IDictionary<string, string>? headers, TimeSpan? delay)
    {
        foreach (var (key, value) in headers ?? new Dictionary<string, string>())
        {
            builder = builder.WithHeader(key, value);
        }

        return delay is null ? builder : builder.WithDelay(delay.Value);
    }

    private string Serialize(object value) => JsonSerializer.Serialize(value, value.GetType(), Options.JsonSerializerOptions);

    private IEnumerable<IRequestMessage> RequestsOf(StoveTestContext test) =>
        EntriesOf(test).Select(e => e.RequestMessage!);

    private IEnumerable<global::WireMock.Logging.ILogEntry> EntriesOf(StoveTestContext test) =>
        Server.LogEntries
            .Where(e => e.RequestMessage is not null)
            .Where(e => test.Owns(Header(e.RequestMessage!, StoveHeaders.TestId), Header(e.RequestMessage!, StoveHeaders.Traceparent)));

    private string DescribeRequests(StoveTestContext test)
    {
        var entries = EntriesOf(test).ToList();
        if (entries.Count == 0)
        {
            return "Requests received: (none)";
        }

        var output = new StringBuilder().AppendLine(CultureInfo.InvariantCulture, $"Requests received ({entries.Count}):");
        foreach (var entry in entries)
        {
            var request = entry.RequestMessage!;
            var matched = entry.MappingGuid is null ? "UNMATCHED" : "matched";
            output.AppendLine(CultureInfo.InvariantCulture,
                $"  {request.Method} {request.Url} -> {entry.ResponseMessage?.StatusCode} ({matched}) {Truncate(request.Body)}");
        }

        return output.ToString().TrimEnd();
    }

    private static string? Header(IRequestMessage request, string name)
    {
        if (request.Headers is null)
        {
            return null;
        }

        var header = request.Headers.FirstOrDefault(h => string.Equals(h.Key, name, StringComparison.OrdinalIgnoreCase));
        return header.Value?.FirstOrDefault();
    }

    private static string Truncate(string? body) =>
        string.IsNullOrEmpty(body) ? string.Empty : body.Length <= 300 ? body : string.Concat(body.AsSpan(0, 300), "...");

    public override ValueTask DisposeAsync()
    {
        _server?.Stop();
        _server?.Dispose();
        return ValueTask.CompletedTask;
    }
}

public static class WireMockStoveExtensions
{
    public static StoveBuilder WithWireMock(this StoveBuilder builder, Action<WireMockOptions>? configure = null) =>
        builder.WithWireMock(name: null, configure);

    public static StoveBuilder WithWireMock(this StoveBuilder builder, string? name, Action<WireMockOptions>? configure = null)
    {
        var options = new WireMockOptions();
        configure?.Invoke(options);
        return builder.WithSystem(new WireMockSystem(name, options));
    }

    public static WireMockSystem WireMock(this StoveTestContext test, string? name = null) => test.GetSystem<WireMockSystem>(name);
}
