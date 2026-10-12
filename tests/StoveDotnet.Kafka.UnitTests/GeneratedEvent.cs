using Avro;
using Avro.Specific;

namespace StoveDotnet.Kafka.UnitTests;

// The contract emitted by avrogen: a public parameterless ISpecificRecord with a schema and field accessors.
public sealed class GeneratedEvent : ISpecificRecord
{
    private static readonly Schema EventSchema = Schema.Parse("""
        {"type":"record","name":"GeneratedEvent","namespace":"StoveDotnet.Kafka.UnitTests","fields":[{"name":"Id","type":"string"}]}
        """);
    public Schema Schema => EventSchema;
    public string Id { get; set; } = "";
    public object Get(int fieldPos) => fieldPos == 0 ? Id : throw new ArgumentOutOfRangeException(nameof(fieldPos));
    public void Put(int fieldPos, object fieldValue)
    {
        ArgumentOutOfRangeException.ThrowIfNotEqual(fieldPos, 0);
        Id = (string)fieldValue;
    }
}
