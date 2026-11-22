using System.Text;

namespace StoveDotnet.Kafka;

/// <summary>A record seen on the broker by Stove's observer.</summary>
public sealed record ObservedRecord(
    string Topic,
    int Partition,
    long Offset,
    byte[]? Key,
    byte[]? Value,
    IReadOnlyDictionary<string, string> Headers,
    DateTimeOffset Timestamp)
{
    /// <summary>The key decoded as UTF-8, or <c>null</c>.</summary>
    public string? KeyAsString => Key is null ? null : Encoding.UTF8.GetString(Key);

    /// <summary>The value decoded as UTF-8, or <c>null</c>.</summary>
    public string? ValueAsString => Value is null ? null : Encoding.UTF8.GetString(Value);

    /// <summary>The value of a header, or <c>null</c>. Names are case-insensitive.</summary>
    public string? Header(string name) => Headers.GetValueOrDefault(name);
}

/// <summary>An observed record whose value was deserialized to <typeparamref name="T"/>.</summary>
public sealed record ObservedMessage<T>(T Value, ObservedRecord Record)
{
    /// <summary>Topic the record was observed on.</summary>
    public string Topic => Record.Topic;

    /// <summary>The key decoded as UTF-8, or <c>null</c>.</summary>
    public string? Key => Record.KeyAsString;

    /// <summary>The record's headers, case-insensitive by name.</summary>
    public IReadOnlyDictionary<string, string> Headers => Record.Headers;
}
