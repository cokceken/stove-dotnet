using Confluent.SchemaRegistry;
using DotNet.Testcontainers.Builders;

namespace StoveDotnet.SchemaRegistry;

/// <summary>Address of the running Schema Registry.</summary>
/// <param name="Url">Base URL of the registry.</param>
public sealed record SchemaRegistryExposedConfiguration(string Url) : IExposedConfiguration;
/// <summary>What a Schema Registry migration receives.</summary>
/// <param name="Client">Stove's registry client.</param>
/// <param name="Configuration">Address of the running registry.</param>
public sealed record SchemaRegistryMigrationContext(ISchemaRegistryClient Client, SchemaRegistryExposedConfiguration Configuration);

/// <summary>Options of the Schema Registry module.</summary>
public sealed class SchemaRegistryOptions : SystemOptions<SchemaRegistryExposedConfiguration>
{
    /// <summary>Container image of the registry.</summary>
    public string Image { get; set; } = "confluentinc/cp-schema-registry:8.1.0";
    /// <summary>Further container customization.</summary>
    public Func<ContainerBuilder, ContainerBuilder>? ConfigureContainer { get; set; }
    /// <summary>Client authentication and TLS settings for the registry HTTP connection.</summary>
    public Action<SchemaRegistryConfig>? ConfigureClient { get; set; }
    /// <summary>Run before the application starts, in ascending order; typically registers schemas.</summary>
    public MigrationCollection<SchemaRegistryMigrationContext> Migrations { get; } = new();
    /// <summary>
    /// Runs when Stove stops, before the container is removed; also against an existing registry.
    /// </summary>
    public Func<ISchemaRegistryClient, CancellationToken, Task>? Cleanup { get; set; }
    internal string? ExistingUrl { get; private set; }
    internal bool RunMigrationsOnExisting { get; private set; } = true;

    /// <summary>Uses an already running Schema Registry instead of starting a container.</summary>
    public SchemaRegistryOptions UseExisting(string url, bool runMigrations = true)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            throw new ArgumentException("Schema Registry URL must be an absolute HTTP or HTTPS URL.", nameof(url));
        ExistingUrl = url;
        RunMigrationsOnExisting = runMigrations;
        return this;
    }
}
