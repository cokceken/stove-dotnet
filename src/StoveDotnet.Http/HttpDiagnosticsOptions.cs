using System.Net.Http.Headers;

namespace StoveDotnet.Http;

/// <summary>Controls diagnostic text only; never changes the request, RawBody or response headers.</summary>
public sealed class HttpDiagnosticsOptions
{
    public int MaxBodyLength { get; set; } = 2048;
    /// <summary>Opt in to buffering request content for diagnostics. Leave false for streaming or binary uploads.</summary>
    public bool IncludeRequestBody { get; set; }
    public ISet<string> SensitiveHeaders { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "Authorization", "Proxy-Authorization", "Cookie", "Set-Cookie", "X-Api-Key", "X-Device-Key" };
    public Func<string, string>? RedactUrl { get; set; }
    public Func<string, string>? RedactRequestBody { get; set; }
    public Func<string, string>? RedactResponseBody { get; set; }

    internal string Body(string body, bool request = false)
    {
        var safe = Apply(request ? RedactRequestBody : RedactResponseBody, body);
        var limit = Math.Max(0, MaxBodyLength);
        return safe.Length <= limit ? safe : safe[..limit] + "… [truncated]";
    }

    internal string Url(Uri? uri)
    {
        if (uri is null) return "(unknown URL)";
        // Query values and user-info may contain credentials. Preserve the path; custom redaction can hide it too.
        var safe = uri.IsAbsoluteUri ? uri.GetLeftPart(UriPartial.Path) : uri.OriginalString.Split('?', '#')[0];
        if (uri.IsAbsoluteUri && !string.IsNullOrEmpty(uri.UserInfo))
            safe = new UriBuilder(uri) { UserName = "", Password = "", Query = "", Fragment = "" }.Uri.GetLeftPart(UriPartial.Path);
        if (uri.OriginalString.Contains('?', StringComparison.Ordinal)) safe += "?[redacted]";
        return Apply(RedactUrl, safe);
    }

    internal string Headers(HttpHeaders headers) => string.Join(Environment.NewLine, headers.Select(h =>
        $"{h.Key}: {(SensitiveHeaders.Contains(h.Key) ? "[redacted]" : Limit(string.Join(", ", h.Value)))}"));

    private static string Limit(string value) => value.Length <= 512 ? value : value[..512] + "… [truncated]";
    private static string Apply(Func<string, string>? redact, string value)
    {
        try { return redact is null ? value : redact(value) ?? "[redacted]"; }
        catch { return "[redaction failed]"; } // Never put a redactor's exception (possibly containing secrets) in output.
    }
}
