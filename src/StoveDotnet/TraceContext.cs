using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;

namespace StoveDotnet;

/// <summary>W3C trace-context helpers.</summary>
public static class TraceContext
{
    public static string NewTraceId() => NewHexId(16);

    public static string NewSpanId() => NewHexId(8);

    public static string FormatTraceparent(string traceId, string spanId) => $"00-{traceId}-{spanId}-01";

    /// <summary>Extracts the trace id from a well-formed <c>traceparent</c> header value.</summary>
    public static bool TryParseTraceId(string? traceparent, [NotNullWhen(true)] out string? traceId)
    {
        traceId = null;
        if (string.IsNullOrEmpty(traceparent))
        {
            return false;
        }

        var parts = traceparent.Trim().Split('-');
        if (parts.Length < 4 || parts[0].Length != 2 || !IsHexId(parts[1], 32) || !IsHexId(parts[2], 16))
        {
            return false;
        }

        traceId = parts[1].ToLowerInvariant();
        return true;
    }

    private static bool IsHexId(string value, int length) =>
        value.Length == length && value.All(Uri.IsHexDigit) && value.Any(c => c != '0');

    private static string NewHexId(int bytes)
    {
        Span<byte> buffer = stackalloc byte[bytes];
        do
        {
            RandomNumberGenerator.Fill(buffer);
        }
        while (buffer.IndexOfAnyExcept((byte)0) < 0);

        return Convert.ToHexStringLower(buffer);
    }
}
