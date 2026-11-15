using Azure.Messaging.ServiceBus.Administration;

namespace StoveDotnet.Azure.ServiceBus;

/// <summary>Typed queues, topics, subscriptions and rules prepared before applications start.</summary>
public sealed class AzureServiceBusTopology
{
    internal IList<AzureServiceBusQueueDefinition> Queues { get; } = [];
    internal IList<AzureServiceBusTopicDefinition> Topics { get; } = [];

    /// <summary>Declares a queue.</summary>
    public AzureServiceBusTopology Queue(string name, Action<CreateQueueOptions>? configure = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var options = new CreateQueueOptions(name);
        configure?.Invoke(options);
        Queues.Add(new(options));
        return this;
    }

    /// <summary>Declares a topic and, through <paramref name="subscriptions"/>, its subscriptions.</summary>
    public AzureServiceBusTopology Topic(
        string name,
        Action<CreateTopicOptions>? configure = null,
        Action<AzureServiceBusTopicTopology>? subscriptions = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var options = new CreateTopicOptions(name);
        configure?.Invoke(options);
        var topology = new AzureServiceBusTopicTopology(name);
        subscriptions?.Invoke(topology);
        Topics.Add(new(options, topology.Subscriptions));
        return this;
    }
}

/// <summary>Declares the subscriptions of one topic.</summary>
public sealed class AzureServiceBusTopicTopology
{
    private readonly string _topic;
    internal IList<AzureServiceBusSubscriptionDefinition> Subscriptions { get; } = [];

    internal AzureServiceBusTopicTopology(string topic) => _topic = topic;

    /// <summary>Declares a subscription and, through <paramref name="rules"/>, its filter rules.</summary>
    public AzureServiceBusTopicTopology Subscription(
        string name,
        Action<CreateSubscriptionOptions>? configure = null,
        Action<AzureServiceBusSubscriptionRules>? rules = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var options = new CreateSubscriptionOptions(_topic, name);
        configure?.Invoke(options);
        var configuredRules = new AzureServiceBusSubscriptionRules();
        rules?.Invoke(configuredRules);
        Subscriptions.Add(new(options, configuredRules.Items));
        return this;
    }
}

/// <summary>
/// Declares the filter rules of one subscription. Adding a rule replaces the default match-all rule.
/// </summary>
public sealed class AzureServiceBusSubscriptionRules
{
    internal IList<CreateRuleOptions> Items { get; } = [];

    /// <summary>Adds a SQL filter rule.</summary>
    public AzureServiceBusSubscriptionRules Sql(string name, string expression)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(expression);
        Items.Add(new CreateRuleOptions(name, new SqlRuleFilter(expression)));
        return this;
    }

    /// <summary>Adds a correlation filter rule.</summary>
    public AzureServiceBusSubscriptionRules Correlation(string name, Action<CorrelationRuleFilter> configure)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(configure);
        var filter = new CorrelationRuleFilter();
        configure(filter);
        Items.Add(new CreateRuleOptions(name, filter));
        return this;
    }

    /// <summary>Adds a rule built with the SDK's own options.</summary>
    public AzureServiceBusSubscriptionRules Add(CreateRuleOptions rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        Items.Add(rule);
        return this;
    }
}

internal sealed record AzureServiceBusQueueDefinition(CreateQueueOptions Options);
internal sealed record AzureServiceBusTopicDefinition(CreateTopicOptions Options, IList<AzureServiceBusSubscriptionDefinition> Subscriptions);
internal sealed record AzureServiceBusSubscriptionDefinition(CreateSubscriptionOptions Options, IList<CreateRuleOptions> Rules);
