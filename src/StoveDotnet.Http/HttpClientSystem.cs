using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace StoveDotnet.Http;

public sealed class HttpClientOptions
{
    public HttpDiagnosticsOptions Diagnostics { get; } = new();
    /// <summary>Name of the application to bind to. Null resolves the default or only application.</summary>
    public string? ApplicationName { get; set; }
    /// <summary>Base address for relative URIs. Defaults to the application under test's Kestrel address.</summary>
    public Uri? BaseAddress { get; set; }

    public JsonSerializerOptions JsonSerializerOptions { get; set; } = new(JsonSerializerDefaults.Web);

    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Headers sent with every request of this client, e.g. an Authorization header.</summary>
    public IDictionary<string, string> DefaultHeaders { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Calls the application under test over real HTTP, tagging every request with the running test's trace. The exchanges
/// of a failed test are attached to its failure output, whichever assertion library detected the failure.
/// </summary>
public sealed class HttpClientSystem : IPluggedSystem, IAfterApplicationsStarted, ITestScopeAware, IFailureDetailsProvider
{
    private const int MaxExchangesPerTest = 20;
    private const int MaxSuccessBodyLength = 300;
    private readonly ConcurrentDictionary<string, ConcurrentQueue<string>> _exchanges = new(StringComparer.Ordinal);
    private readonly HttpClientOptions _options;
    private HttpClient? _client;

    public HttpClientSystem(string? name, HttpClientOptions options)
    {
        Name = name;
        _options = options;
    }

    public string? Name { get; }

    public Task OnApplicationsStartedAsync(Stove stove, CancellationToken cancellationToken)
    {
        if (_options.BaseAddress is not null) return Task.CompletedTask;
        if (_options.ApplicationName is null && stove.Application is null) return Task.CompletedTask;
        _options.BaseAddress = stove.GetApplication(_options.ApplicationName).BaseAddress;
        return Task.CompletedTask;
    }

    public Task<StoveHttpResponse<T>> Get<T>(string uri, IDictionary<string, string>? headers = null) =>
        Send<T>(HttpMethod.Get, uri, body: null, headers);

    public Task<StoveHttpResponse> Get(string uri, IDictionary<string, string>? headers = null) =>
        Send(HttpMethod.Get, uri, body: null, headers);

    public Task<StoveHttpResponse<T>> Post<T>(string uri, object? body = null, IDictionary<string, string>? headers = null) =>
        Send<T>(HttpMethod.Post, uri, body, headers);

    public Task<StoveHttpResponse> Post(string uri, object? body = null, IDictionary<string, string>? headers = null) =>
        Send(HttpMethod.Post, uri, body, headers);

    public Task<StoveHttpResponse<T>> Put<T>(string uri, object? body = null, IDictionary<string, string>? headers = null) =>
        Send<T>(HttpMethod.Put, uri, body, headers);

    public Task<StoveHttpResponse> Put(string uri, object? body = null, IDictionary<string, string>? headers = null) =>
        Send(HttpMethod.Put, uri, body, headers);

    public Task<StoveHttpResponse<T>> Patch<T>(string uri, object? body = null, IDictionary<string, string>? headers = null) =>
        Send<T>(HttpMethod.Patch, uri, body, headers);

    public Task<StoveHttpResponse> Patch(string uri, object? body = null, IDictionary<string, string>? headers = null) =>
        Send(HttpMethod.Patch, uri, body, headers);

    public Task<StoveHttpResponse<T>> Delete<T>(string uri, IDictionary<string, string>? headers = null) =>
        Send<T>(HttpMethod.Delete, uri, body: null, headers);

    public Task<StoveHttpResponse> Delete(string uri, IDictionary<string, string>? headers = null) =>
        Send(HttpMethod.Delete, uri, body: null, headers);

    public async Task<StoveHttpResponse<T>> Send<T>(HttpMethod method, string uri, object? body = null, IDictionary<string, string>? headers = null)
    {
        var response = await Send(method, uri, body, headers).ConfigureAwait(false);
        return new StoveHttpResponse<T>(response, _options.JsonSerializerOptions);
    }

    public async Task<StoveHttpResponse> Send(HttpMethod method, string uri, object? body = null, IDictionary<string, string>? headers = null)
    {
        using var request = new HttpRequestMessage(method, uri);
        if (body is not null)
        {
            request.Content = body as HttpContent ?? JsonContent.Create(body, body.GetType(), options: _options.JsonSerializerOptions);
        }

        foreach (var (key, value) in _options.DefaultHeaders.Concat(headers ?? new Dictionary<string, string>()))
        {
            request.Headers.Remove(key);
            request.Headers.TryAddWithoutValidation(key, value);
        }

        return await Send(request).ConfigureAwait(false);
    }

    /// <summary>Sends caller-owned content with correlation and returns a buffered Stove response. The caller owns the request.</summary>
    public async Task<StoveHttpResponse> Send(HttpRequestMessage request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var test = StoveTestContext.Require();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(test.CancellationToken, cancellationToken);
        var requestBody = _options.Diagnostics.IncludeRequestBody && request.Content is not null
            ? _options.Diagnostics.Body(await request.Content.ReadAsStringAsync(linked.Token).ConfigureAwait(false), request: true) : null;
        var watch = Stopwatch.StartNew();
        using var response = await SendCore(test, request, requestBody, watch, linked.Token).ConfigureAwait(false);
        var rawBody = await response.Content.ReadAsStringAsync(linked.Token).ConfigureAwait(false);
        var shownBody = _options.Diagnostics.Body(rawBody);
        if (response.IsSuccessStatusCode && shownBody.Length > MaxSuccessBodyLength) shownBody = shownBody[..MaxSuccessBodyLength] + "… [truncated]";
        Record(test, request, requestBody, watch, $"{(int)response.StatusCode} {response.StatusCode}",
            $"Response body: {(rawBody.Length == 0 ? "(empty)" : shownBody)}");
        var responseHeaders = response.Headers.Concat(response.Content.Headers)
            .ToDictionary(h => h.Key, h => (IReadOnlyList<string>)h.Value.ToList(), StringComparer.OrdinalIgnoreCase);
        var diagnostic = $"{request.Method} {_options.Diagnostics.Url(request.RequestUri)} → {(int)response.StatusCode} {response.StatusCode}\n"
            + $"Request headers:\n{_options.Diagnostics.Headers(request.Headers)}\n"
            + (requestBody is null ? "" : $"Request body: {requestBody}\n")
            + $"Response headers:\n{_options.Diagnostics.Headers(response.Headers)}\n{_options.Diagnostics.Headers(response.Content.Headers)}\n"
            + $"Response body: {_options.Diagnostics.Body(rawBody)}";
        return new StoveHttpResponse(response.StatusCode, responseHeaders, rawBody, diagnostic);
    }

    public async Task<StoveHttpResponse<T>> Send<T>(HttpRequestMessage request, CancellationToken cancellationToken = default) =>
        new(await Send(request, cancellationToken).ConfigureAwait(false), _options.JsonSerializerOptions);

    /// <summary>Sends a hand-built request; correlation headers are still added. The caller reads and disposes the response.</summary>
    public Task<HttpResponseMessage> SendRaw(HttpRequestMessage request) => SendRaw(request, default);

    public async Task<HttpResponseMessage> SendRaw(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var test = StoveTestContext.Require();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(test.CancellationToken, cancellationToken);
        var watch = Stopwatch.StartNew();
        var response = await SendCore(test, request, requestBody: null, watch, linked.Token).ConfigureAwait(false);
        Record(test, request, requestBody: null, watch, $"{(int)response.StatusCode} {response.StatusCode}", "Response body: (not read by Stove; raw response)");
        return response;
    }

    private async Task<HttpResponseMessage> SendCore(StoveTestContext test, HttpRequestMessage request, string? requestBody, Stopwatch watch, CancellationToken cancellationToken)
    {
        foreach (var (key, value) in _options.DefaultHeaders)
            if (!request.Headers.Contains(key)) request.Headers.TryAddWithoutValidation(key, value);
        // The runtime's HTTP handler starts a client span under the activity below and writes that span into
        // traceparent, unless the header is already there. Leaving the header to it makes the application's request
        // span a child of the call that caused it; Stove writes the header itself only where the runtime will not.
        var runtimePropagates = RuntimePropagatesTraceContext();
        foreach (var (key, value) in test.CorrelationHeaders())
        {
            request.Headers.Remove(key);
            if (runtimePropagates && string.Equals(key, StoveHeaders.Traceparent, StringComparison.OrdinalIgnoreCase)) continue;
            request.Headers.TryAddWithoutValidation(key, value);
        }

        // With this activity current, the client span and everything the application does for the request stay in
        // the test's trace.
        using var activity = test.StartCorrelatedActivity("stove.http");
        try
        {
            return await Client().SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // No response to show: a refused connection or a timeout is the evidence.
            Record(test, request, requestBody, watch, $"no response: {ex.GetType().Name}: {ex.Message}", detail: null);
            throw;
        }
    }

    private static bool RuntimePropagatesTraceContext()
    {
        if (AppContext.TryGetSwitch("System.Net.Http.EnableActivityPropagation", out var enabled) && !enabled) return false;
        var variable = Environment.GetEnvironmentVariable("DOTNET_SYSTEM_NET_HTTP_ENABLEACTIVITYPROPAGATION");
        if (variable is "0" || string.Equals(variable, "false", StringComparison.OrdinalIgnoreCase)) return false;
        // An application may replace the propagator, for example with one that writes no headers or another format.
        return DistributedContextPropagator.Current.Fields.Contains(StoveHeaders.Traceparent, StringComparer.OrdinalIgnoreCase);
    }

    private void Record(StoveTestContext test, HttpRequestMessage request, string? requestBody, Stopwatch watch, string outcome, string? detail)
    {
        var line = string.Create(CultureInfo.InvariantCulture,
            $"  {request.Method} {_options.Diagnostics.Url(request.RequestUri)} → {outcome} ({watch.Elapsed.TotalMilliseconds:0.#}ms)");
        if (requestBody is not null) line += $"{Environment.NewLine}    Request body: {requestBody}";
        if (detail is not null) line += $"{Environment.NewLine}    {detail}";
        var exchanges = _exchanges.GetOrAdd(test.TestId, _ => new ConcurrentQueue<string>());
        exchanges.Enqueue(line);
        while (exchanges.Count > MaxExchangesPerTest) exchanges.TryDequeue(out _);
    }

    public Task OnTestStartedAsync(StoveTestContext test) => Task.CompletedTask;

    public Task OnTestEndedAsync(StoveTestContext test, Exception? failure)
    {
        ArgumentNullException.ThrowIfNull(test);
        // Failure details are collected after the scope ends; keep a failed test's exchanges until then.
        if (failure is null) _exchanges.TryRemove(test.TestId, out _);
        return Task.CompletedTask;
    }

    public Task<FailureDetails?> DescribeAsync(StoveTestContext test, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(test);
        if (!_exchanges.TryRemove(test.TestId, out var exchanges) || exchanges.IsEmpty) return Task.FromResult<FailureDetails?>(null);
        var lines = exchanges.ToArray();
        var header = lines.Length < MaxExchangesPerTest ? $"Requests sent ({lines.Length}):" : $"Requests sent (latest {lines.Length}):";
        return Task.FromResult<FailureDetails?>(new FailureDetails(
            Name is null ? "http" : $"http '{Name}'", header + Environment.NewLine + string.Join(Environment.NewLine, lines)));
    }

    private HttpClient Client() => _client ??= new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false })
    {
        BaseAddress = _options.BaseAddress
            ?? throw new InvalidOperationException("HttpClient has no BaseAddress and Stove was started without an application."),
        Timeout = _options.Timeout,
    };

    public ValueTask DisposeAsync()
    {
        _client?.Dispose();
        return ValueTask.CompletedTask;
    }
}

public class StoveHttpResponse(HttpStatusCode statusCode, IReadOnlyDictionary<string, IReadOnlyList<string>> headers, string rawBody)
{
    private readonly string _diagnostic = $"{(int)statusCode} {statusCode}: {new HttpDiagnosticsOptions().Body(rawBody)}";

    internal StoveHttpResponse(HttpStatusCode statusCode, IReadOnlyDictionary<string, IReadOnlyList<string>> headers, string rawBody, string diagnostic)
        : this(statusCode, headers, rawBody) => _diagnostic = diagnostic;

    protected StoveHttpResponse(StoveHttpResponse response) : this(response.StatusCode, response.Headers, response.RawBody, response._diagnostic) { }

    public StoveHttpResponse Expect(HttpStatusCode expected)
    {
        if (StatusCode != expected)
            throw new StoveAssertionException($"Expected HTTP {(int)expected} {expected}, actual {(int)StatusCode} {StatusCode}.\n{this}");
        return this;
    }
    public HttpStatusCode StatusCode { get; } = statusCode;

    public IReadOnlyDictionary<string, IReadOnlyList<string>> Headers { get; } = headers;

    public string RawBody { get; } = rawBody;

    public bool IsSuccessStatusCode => (int)StatusCode is >= 200 and < 300;

    public override string ToString() => _diagnostic;
}

public sealed class StoveHttpResponse<T>(StoveHttpResponse response, JsonSerializerOptions jsonSerializerOptions)
    : StoveHttpResponse(response)
{
    public new StoveHttpResponse<T> Expect(HttpStatusCode expected) { base.Expect(expected); return this; }
    private readonly Lazy<T> _body = new(() => Deserialize(response, jsonSerializerOptions));

    /// <summary>The JSON body deserialized on first access; failures preserve the original exception and include bounded, redacted response diagnostics.</summary>
    public T Body => _body.Value;

    private static T Deserialize(StoveHttpResponse response, JsonSerializerOptions options)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(response.RawBody, options)
                ?? throw new JsonException("The response body is null.");
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"Could not deserialize the response body to {typeof(T).FullName}: {ex.Message}\n"
                + $"JSON path: {ex.Path ?? "(unknown)"}; line: {ex.LineNumber}; byte: {ex.BytePositionInLine}.\nResponse: {response}", ex);
        }
    }
}

public static class HttpStoveExtensions
{
    public static StoveBuilder WithHttpClient(this StoveBuilder builder, Action<HttpClientOptions>? configure = null) =>
        builder.WithHttpClient(name: null, configure);

    public static StoveBuilder WithHttpClient(this StoveBuilder builder, string? name, Action<HttpClientOptions>? configure = null)
    {
        var options = new HttpClientOptions();
        configure?.Invoke(options);
        return builder.WithSystem(new HttpClientSystem(name, options));
    }

    public static HttpClientSystem Http(this StoveTestContext test, string? name = null) => test.GetSystem<HttpClientSystem>(name);
}
