using System.Text.Json;
using Azure.Core;
using Azure.Messaging.ServiceBus;
using Azure.Messaging.ServiceBus.Administration;
using Testcontainers.ServiceBus;

namespace StoveDotnet.Azure.ServiceBus;

/// <summary>Connection details of the running namespace.</summary>
/// <param name="FullyQualifiedNamespace">Host name of the namespace, for clients that authenticate with a
/// <c>TokenCredential</c>.</param>
/// <param name="ConnectionString">Connection string, when one exists (emulator or connection-string mode);
/// <c>null</c> in credential mode.</param>
/// <param name="IsEmulator">Whether Stove started the emulator, as opposed to using an existing namespace.</param>
public sealed record AzureServiceBusExposedConfiguration(
    string FullyQualifiedNamespace,
    string? ConnectionString,
    bool IsEmulator) : IExposedConfiguration;

/// <summary>What a Service Bus migration or cleanup callback receives.</summary>
/// <param name="Client">Stove's client.</param>
/// <param name="AdministrationClient">Stove's administration client, for entities and rules.</param>
/// <param name="Configuration">Connection details of the running namespace.</param>
public sealed record AzureServiceBusMigrationContext(
    ServiceBusClient Client,
    ServiceBusAdministrationClient AdministrationClient,
    AzureServiceBusExposedConfiguration Configuration);

/// <summary>Options of the Azure Service Bus module.</summary>
public sealed class AzureServiceBusOptions : SystemOptions<AzureServiceBusExposedConfiguration>
{
    /// <summary>The official emulator image. Pin or override this for reproducible environments.</summary>
    public string Image { get; set; } = "mcr.microsoft.com/azure-messaging/servicebus-emulator:2.0.0";
    /// <summary>Must be explicitly enabled before using the managed emulator.</summary>
    public bool AcceptLicenseAgreement { get; set; }
    /// <summary>Further emulator container customization.</summary>
    public Func<ServiceBusBuilder, ServiceBusBuilder>? ConfigureContainer { get; set; }
    /// <summary>
    /// Customizes Stove's own client (transport, retries) before it connects. It does not configure the
    /// application's client.
    /// </summary>
    public Action<ServiceBusClientOptions>? ConfigureClient { get; set; }
    /// <summary>Customizes Stove's administration client.</summary>
    public Action<ServiceBusAdministrationClientOptions>? ConfigureAdministrationClient { get; set; }
    /// <summary>Serializer options for message bodies. Defaults to web defaults.</summary>
    public JsonSerializerOptions JsonSerializerOptions { get; set; } = new(JsonSerializerDefaults.Web);
    /// <summary>
    /// Default wait of the entity's <c>Should*</c> and <c>Receive</c> methods. Defaults to 10 seconds.
    /// </summary>
    public TimeSpan DefaultTimeout { get; set; } = TimeSpan.FromSeconds(10);
    /// <summary>Maximum number of messages one peek inspects. Defaults to 1000.</summary>
    public int MaxPeekMessages { get; set; } = 1_000;
    /// <summary>
    /// How messages without correlation properties are treated. Defaults to <see
    /// cref="UncorrelatedMessagePolicy.Exclude"/>.
    /// </summary>
    public UncorrelatedMessagePolicy UncorrelatedMessages { get; set; } = UncorrelatedMessagePolicy.Exclude;
    /// <summary>Cancels correlated scheduled messages discovered by Stove when a test scope ends.</summary>
    public bool CancelScheduledMessagesAfterTest { get; set; } = true;
    /// <summary>Queues, topics, subscriptions and rules to create before the application starts.</summary>
    public AzureServiceBusTopology Topology { get; } = new();
    /// <summary>Run after the topology, before the application starts, in ascending order.</summary>
    public MigrationCollection<AzureServiceBusMigrationContext> Migrations { get; } = new();
    /// <summary>Runs when Stove stops; also against an existing namespace.</summary>
    public Func<AzureServiceBusMigrationContext, CancellationToken, Task>? Cleanup { get; set; }

    internal string? ExistingConnectionString { get; private set; }
    internal string? ExistingFullyQualifiedNamespace { get; private set; }
    internal TokenCredential? ExistingCredential { get; private set; }
    internal bool RunMigrationsOnExisting { get; private set; }

    /// <summary>
    /// Uses an existing namespace through its connection string instead of starting the emulator. Topology and
    /// migrations are skipped unless <paramref name="runMigrations"/> is true.
    /// </summary>
    public AzureServiceBusOptions UseExisting(string connectionString, bool runMigrations = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ExistingConnectionString = connectionString;
        ExistingFullyQualifiedNamespace = null;
        ExistingCredential = null;
        RunMigrationsOnExisting = runMigrations;
        return this;
    }

    /// <summary>
    /// Uses an existing namespace with any <c>TokenCredential</c> (managed identity, developer login) instead of
    /// starting the emulator. Topology and migrations are skipped unless <paramref name="runMigrations"/> is true.
    /// </summary>
    public AzureServiceBusOptions UseExisting(
        string fullyQualifiedNamespace,
        TokenCredential credential,
        bool runMigrations = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fullyQualifiedNamespace);
        ArgumentNullException.ThrowIfNull(credential);
        ExistingConnectionString = null;
        ExistingFullyQualifiedNamespace = fullyQualifiedNamespace;
        ExistingCredential = credential;
        RunMigrationsOnExisting = runMigrations;
        return this;
    }
}
