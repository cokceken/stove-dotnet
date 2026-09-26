using System.Text.Json;
using Azure.Core;
using Azure.Messaging.ServiceBus;
using Azure.Messaging.ServiceBus.Administration;
using Testcontainers.ServiceBus;

namespace StoveDotnet.Azure.ServiceBus;

public sealed record AzureServiceBusExposedConfiguration(
    string FullyQualifiedNamespace,
    string? ConnectionString,
    bool IsEmulator) : IExposedConfiguration;

public sealed record AzureServiceBusSetupContext(
    ServiceBusClient Client,
    ServiceBusAdministrationClient AdministrationClient,
    AzureServiceBusExposedConfiguration Configuration);

public sealed class AzureServiceBusOptions : SystemOptions<AzureServiceBusExposedConfiguration>
{
    /// <summary>The official emulator image. Pin or override this for reproducible environments.</summary>
    public string Image { get; set; } = "mcr.microsoft.com/azure-messaging/servicebus-emulator:2.0.0";
    /// <summary>Must be explicitly enabled before using the managed emulator.</summary>
    public bool AcceptLicenseAgreement { get; set; }
    public Func<ServiceBusBuilder, ServiceBusBuilder>? ConfigureContainer { get; set; }
    public Action<ServiceBusClientOptions>? ConfigureClient { get; set; }
    public Action<ServiceBusAdministrationClientOptions>? ConfigureAdministrationClient { get; set; }
    public JsonSerializerOptions JsonSerializerOptions { get; set; } = new(JsonSerializerDefaults.Web);
    public TimeSpan DefaultTimeout { get; set; } = TimeSpan.FromSeconds(10);
    public int MaxPeekMessages { get; set; } = 1_000;
    public UncorrelatedMessagePolicy UncorrelatedMessages { get; set; } = UncorrelatedMessagePolicy.Exclude;
    /// <summary>Cancels correlated scheduled messages discovered by Stove when a test scope ends.</summary>
    public bool CancelScheduledMessagesAfterTest { get; set; } = true;
    public AzureServiceBusTopology Topology { get; } = new();
    public MigrationCollection<AzureServiceBusSetupContext> Setup { get; } = new();
    public Func<AzureServiceBusSetupContext, CancellationToken, Task>? Cleanup { get; set; }

    internal string? ExistingConnectionString { get; private set; }
    internal string? ExistingFullyQualifiedNamespace { get; private set; }
    internal TokenCredential? ExistingCredential { get; private set; }
    internal bool RunTopologySetupOnExisting { get; private set; }

    public AzureServiceBusOptions UseExisting(string connectionString, bool runTopologySetup = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ExistingConnectionString = connectionString;
        ExistingFullyQualifiedNamespace = null;
        ExistingCredential = null;
        RunTopologySetupOnExisting = runTopologySetup;
        return this;
    }

    public AzureServiceBusOptions UseExisting(
        string fullyQualifiedNamespace,
        TokenCredential credential,
        bool runTopologySetup = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fullyQualifiedNamespace);
        ArgumentNullException.ThrowIfNull(credential);
        ExistingConnectionString = null;
        ExistingFullyQualifiedNamespace = fullyQualifiedNamespace;
        ExistingCredential = credential;
        RunTopologySetupOnExisting = runTopologySetup;
        return this;
    }
}
