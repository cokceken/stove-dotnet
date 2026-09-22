using System.Diagnostics;
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

/// <summary>Calls the application under test over real HTTP, tagging every request with the running test's trace.</summary>
public sealed class HttpClientSystem : IPluggedSystem, IAfterApplicationStarted, IAfterApplicationsStarted
{
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
        return OnApplicationStartedAsync(stove.GetApplication(_options.ApplicationName), cancellationToken);
    }

    public Task OnApplicationStartedAsync(IApplicationContext application, CancellationToken cancellationToken)
    {
        _options.BaseAddress ??= application.BaseAddress;
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
        using var response = await SendRaw(request, linked.Token).ConfigureAwait(false);
        var rawBody = await response.Content.ReadAsStringAsync(linked.Token).ConfigureAwait(false);
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

    /// <summary>Sends a hand-built request; correlation headers are still added.</summary>
    public Task<HttpResponseMessage> SendRaw(HttpRequestMessage request) => SendRaw(request, default);

    public async Task<HttpResponseMessage> SendRaw(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var test = StoveTestContext.Require();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(test.CancellationToken, cancellationToken);
        foreach (var (key, value) in _options.DefaultHeaders)
            if (!request.Headers.Contains(key)) request.Headers.TryAddWithoutValidation(key, value);
        foreach (var (key, value) in test.CorrelationHeaders())
        {
            request.Headers.Remove(key);
            request.Headers.TryAddWithoutValidation(key, value);
        }

        // The application's HttpClient instrumentation also observes this call and re-propagates Activity.Current;
        // keep it inside the test's trace.
        using var activity = test.StartCorrelatedActivity("stove.http");
        return await Client().SendAsync(request, linked.Token).ConfigureAwait(false);
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

    /// <summary>The JSON body deserialized on first access; failures include bounded, redacted response diagnostics.</summary>
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
                $"Could not deserialize the response body to {typeof(T).Name}: invalid JSON or incompatible body at line {ex.LineNumber}, byte {ex.BytePositionInLine}. Response: {response}");
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
