using System.Text.Json;
using WireMock;

namespace StoveDotnet.WireMock;

/// <summary>A request a WireMock instance received during the current test.</summary>
public sealed class RecordedRequest
{
    private readonly JsonSerializerOptions _jsonSerializerOptions;

    internal RecordedRequest(IRequestMessage message, IReadOnlyDictionary<string, string> pathParameters, JsonSerializerOptions jsonSerializerOptions)
    {
        _jsonSerializerOptions = jsonSerializerOptions;
        Method = message.Method;
        Path = message.Path;
        Url = message.Url;
        PathParameters = pathParameters;
        Query = ToReadOnly(message.Query?.Select(q => new KeyValuePair<string, IEnumerable<string>>(q.Key, q.Value)));
        Headers = ToReadOnly(message.Headers?.Select(h => new KeyValuePair<string, IEnumerable<string>>(h.Key, h.Value)));
        Body = message.Body;
        BodyBytes = message.BodyAsBytes;
    }

    /// <summary>HTTP method.</summary>
    public string Method { get; }

    /// <summary>Request path without the query string.</summary>
    public string Path { get; }

    /// <summary>Full request URL.</summary>
    public string Url { get; }

    /// <summary>Values of the path template placeholders, e.g. <c>productId</c> for <c>/stock/{productId}</c>.</summary>
    public IReadOnlyDictionary<string, string> PathParameters { get; }

    /// <summary>Query parameters, case-insensitive by name.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> Query { get; }

    /// <summary>Request headers, case-insensitive by name.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> Headers { get; }

    /// <summary>The body as text, when WireMock could read it as text.</summary>
    public string? Body { get; }

    /// <summary>The raw body, when the request had one.</summary>
    public byte[]? BodyBytes { get; }

    /// <summary>The first value of a header, or <c>null</c>.</summary>
    public string? Header(string name) => Headers.TryGetValue(name, out var values) && values.Count > 0 ? values[0] : null;

    /// <summary>The first value of a query parameter, or <c>null</c>.</summary>
    public string? QueryValue(string name) => Query.TryGetValue(name, out var values) && values.Count > 0 ? values[0] : null;

    /// <summary>Deserializes the JSON body with the WireMock system's serializer options.</summary>
    public T BodyAs<T>()
    {
        var json = Body ?? (BodyBytes is null ? null : System.Text.Encoding.UTF8.GetString(BodyBytes));
        if (string.IsNullOrEmpty(json))
        {
            throw new InvalidOperationException($"{Method} {Url} has no body to deserialize as {typeof(T).Name}.");
        }

        try
        {
            return JsonSerializer.Deserialize<T>(json, _jsonSerializerOptions)
                ?? throw new JsonException("The body is null.");
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Could not deserialize the body of {Method} {Url} as {typeof(T).Name}: {json}", ex);
        }
    }

    /// <summary>Method and URL.</summary>
    public override string ToString() => $"{Method} {Url}";

    private static Dictionary<string, IReadOnlyList<string>> ToReadOnly(IEnumerable<KeyValuePair<string, IEnumerable<string>>>? values)
    {
        var result = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, list) in values ?? [])
        {
            result[key] = list.ToList();
        }

        return result;
    }
}
