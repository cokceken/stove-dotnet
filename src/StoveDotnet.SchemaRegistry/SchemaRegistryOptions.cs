using Confluent.SchemaRegistry;
using DotNet.Testcontainers.Builders;

namespace StoveDotnet.SchemaRegistry;

public sealed record SchemaRegistryExposedConfiguration(string Url) : IExposedConfiguration;
public sealed record SchemaRegistryMigrationContext(ISchemaRegistryClient Client, SchemaRegistryExposedConfiguration Configuration);

public sealed class SchemaRegistryOptions : SystemOptions<SchemaRegistryExposedConfiguration>
{
    public string Image { get; set; } = "confluentinc/cp-schema-registry:8.1.0";
    public Func<ContainerBuilder, ContainerBuilder>? ConfigureContainer { get; set; }
    /// <summary>Client authentication and TLS settings for the registry HTTP connection.</summary>
    public Action<SchemaRegistryConfig>? ConfigureClient { get; set; }
    public MigrationCollection<SchemaRegistryMigrationContext> Migrations { get; } = new();
    public Func<ISchemaRegistryClient, CancellationToken, Task>? Cleanup { get; set; }
    internal string? ExistingUrl { get; private set; }
    internal bool RunMigrationsOnExisting { get; private set; } = true;

    public SchemaRegistryOptions UseExisting(string url, bool runMigrations = true)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            throw new ArgumentException("Schema Registry URL must be an absolute HTTP or HTTPS URL.", nameof(url));
        ExistingUrl = url;
        RunMigrationsOnExisting = runMigrations;
        return this;
    }
}
