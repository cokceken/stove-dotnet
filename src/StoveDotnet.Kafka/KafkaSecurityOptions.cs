using System.Text;
using Confluent.Kafka;
using Testcontainers.Kafka;

namespace StoveDotnet.Kafka;

/// <summary>How Stove's clients, and a broker Stove starts, secure their connections.</summary>
public enum KafkaSecurityMode
{
    /// <summary>No encryption or authentication.</summary>
    Plaintext,

    /// <summary>TLS: clients verify the broker certificate.</summary>
    Tls,

    /// <summary>TLS with client certificates: the broker also verifies the client.</summary>
    MutualTls,
}

/// <summary>PEM certificates for Stove clients and, when managed, the broker's external listener.</summary>
public sealed class KafkaSecurityOptions
{
    /// <summary>
    /// Whether connections are plaintext, TLS, or TLS with client certificates. Defaults to plaintext.
    /// </summary>
    public KafkaSecurityMode Mode { get; set; }
    /// <summary>PEM file of the certificate authority that signed the broker certificate.</summary>
    public string? CaCertificateLocation { get; set; }
    /// <summary>PEM file of the client certificate, for mutual TLS.</summary>
    public string? ClientCertificateLocation { get; set; }
    /// <summary>PEM file of the client private key, for mutual TLS.</summary>
    public string? ClientKeyLocation { get; set; }
    /// <summary>Password of the client private key, when it is encrypted.</summary>
    public string? ClientKeyPassword { get; set; }
    /// <summary>PEM file of the broker certificate. Required when Stove starts a secured broker.</summary>
    public string? BrokerCertificateLocation { get; set; }
    /// <summary>PEM file of the broker private key. Required when Stove starts a secured broker.</summary>
    public string? BrokerKeyLocation { get; set; }
    /// <summary>Password of the broker private key, when it is encrypted.</summary>
    public string? BrokerKeyPassword { get; set; }

    internal void Validate(bool managed)
    {
        if (!Enum.IsDefined(Mode)) throw new InvalidOperationException("Unknown Kafka security mode.");
        if (Mode == KafkaSecurityMode.Plaintext) return;
        RequireFile(CaCertificateLocation, nameof(CaCertificateLocation));
        if (Mode == KafkaSecurityMode.MutualTls)
        {
            RequireFile(ClientCertificateLocation, nameof(ClientCertificateLocation));
            RequireFile(ClientKeyLocation, nameof(ClientKeyLocation));
        }
        if (managed)
        {
            RequireFile(BrokerCertificateLocation, nameof(BrokerCertificateLocation));
            RequireFile(BrokerKeyLocation, nameof(BrokerKeyLocation));
        }
    }

    /// <summary>Applies the selected transport to an application or test Kafka client. Hostname verification stays enabled.</summary>
    public void ApplyTo(ClientConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        config.SecurityProtocol = Mode == KafkaSecurityMode.Plaintext ? SecurityProtocol.Plaintext : SecurityProtocol.Ssl;
        if (Mode == KafkaSecurityMode.Plaintext) return;
        config.SslCaLocation = CaCertificateLocation;
        config.EnableSslCertificateVerification = true;
        config.SslEndpointIdentificationAlgorithm = SslEndpointIdentificationAlgorithm.Https;
        if (Mode == KafkaSecurityMode.MutualTls)
        {
            config.SslCertificateLocation = ClientCertificateLocation;
            config.SslKeyLocation = ClientKeyLocation;
            config.SslKeyPassword = ClientKeyPassword;
        }
    }

    internal KafkaBuilder ConfigureBroker(KafkaBuilder builder)
    {
        if (Mode == KafkaSecurityMode.Plaintext) return builder;
        // Keep the listener name used by Testcontainers' advertised-address script; change its protocol.
        var pem = Encoding.UTF8.GetBytes(File.ReadAllText(BrokerKeyLocation!) + "\n" + File.ReadAllText(BrokerCertificateLocation!));
        builder = builder
            .WithEnvironment("KAFKA_LISTENER_SECURITY_PROTOCOL_MAP", "BROKER:PLAINTEXT,CONTROLLER:PLAINTEXT,PLAINTEXT:SSL")
            .WithEnvironment("KAFKA_SSL_KEYSTORE_TYPE", "PEM")
            .WithEnvironment("KAFKA_SSL_KEYSTORE_LOCATION", "/etc/stove/broker.pem")
            .WithEnvironment("KAFKA_SSL_TRUSTSTORE_TYPE", "PEM")
            .WithEnvironment("KAFKA_SSL_TRUSTSTORE_LOCATION", "/etc/stove/ca.pem")
            .WithEnvironment("KAFKA_LISTENER_NAME_PLAINTEXT_SSL_CLIENT_AUTH", Mode == KafkaSecurityMode.MutualTls ? "required" : "none")
            .WithResourceMapping(pem, "/etc/stove/broker.pem")
            .WithResourceMapping(File.ReadAllBytes(CaCertificateLocation!), "/etc/stove/ca.pem");
        if (BrokerKeyPassword is not null) builder = builder.WithEnvironment("KAFKA_SSL_KEY_PASSWORD", BrokerKeyPassword);
        return builder;
    }

    private static void RequireFile(string? path, string name)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException($"{name} is required for the selected Kafka security mode.", name);
        if (!File.Exists(path)) throw new FileNotFoundException($"Kafka certificate file for {name} was not found.", path);
    }
}
