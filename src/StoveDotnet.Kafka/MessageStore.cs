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
    public string? KeyAsString => Key is null ? null : Encoding.UTF8.GetString(Key);

    public string? ValueAsString => Value is null ? null : Encoding.UTF8.GetString(Value);

    public string? Header(string name) => Headers.GetValueOrDefault(name);
}

/// <summary>An observed record whose value was deserialized to <typeparamref name="T"/>.</summary>
public sealed record ObservedMessage<T>(T Value, ObservedRecord Record)
{
    public string Topic => Record.Topic;

    public string? Key => Record.KeyAsString;

    public IReadOnlyDictionary<string, string> Headers => Record.Headers;
}
