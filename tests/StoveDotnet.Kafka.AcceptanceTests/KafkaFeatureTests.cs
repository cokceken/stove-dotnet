using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Confluent.Kafka;
using Confluent.Kafka.Admin;
using Confluent.SchemaRegistry;
using Confluent.SchemaRegistry.Serdes;
using StoveDotnet.Kafka.Avro;
using StoveDotnet.Kafka.UnitTests;
using StoveDotnet.SchemaRegistry;
using Xunit;

namespace StoveDotnet.Kafka.AcceptanceTests;

public sealed class KafkaFeatureTests
{
    [Theory]
    [InlineData(KafkaSecurityMode.Tls)]
    [InlineData(KafkaSecurityMode.MutualTls)]
    public async Task Secured_broker_supports_publish_observation_admin_and_application_consumer(KafkaSecurityMode mode)
    {
        using var certificates = new Certificates();
        await using var stove = await StoveBuilder.Create().WithKafka(o =>
        {
            certificates.Configure(o.Security, mode);
            o.Migrations.Add((ctx, _) => ctx.Admin.CreateTopicsAsync([new TopicSpecification { Name = "secure", NumPartitions = 1, ReplicationFactor = 1 }]));
        }).StartAsync(TestContext.Current.CancellationToken);
        await stove.Test(async t =>
        {
            var kafka = t.Kafka();
            var config = new ConsumerConfig { BootstrapServers = kafka.ExposedConfiguration.BootstrapServers, GroupId = "secure-app", AutoOffsetReset = AutoOffsetReset.Earliest, EnableAutoCommit = false };
            kafka.ExposedConfiguration.Security.ApplyTo(config);
            using var consumer = new ConsumerBuilder<string, string>(config).Build();
            consumer.Subscribe("secure");
            await kafka.Publish("secure", new OrderCreated("secure", 1));
            var received = await Task.Run(() => consumer.Consume(TimeSpan.FromSeconds(30)), t.CancellationToken);
            Assert.NotNull(received);
            Assert.Contains("secure", received.Message.Value, StringComparison.Ordinal);
            consumer.Commit(received);
            await kafka.ShouldBeConsumed<OrderCreated>(m => m.Value.OrderId == "secure", consumerGroup: "secure-app");
            consumer.Close();
            if (mode == KafkaSecurityMode.MutualTls)
            {
                using var unauthenticated = new AdminClientBuilder(new AdminClientConfig
                {
                    BootstrapServers = kafka.ExposedConfiguration.BootstrapServers,
                    SecurityProtocol = SecurityProtocol.Ssl, SslCaLocation = certificates.CaPath
                }).Build();
                Assert.Throws<KafkaException>(() => unauthenticated.GetMetadata(TimeSpan.FromSeconds(5)));
            }
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Registry_migrations_finish_before_application_and_native_avro_is_observed()
    {
        var app = new RegistryStartupApplication();
        await using var stove = await StoveBuilder.Create()
            .WithKafka(o => o.Migrations.Add((ctx, _) => ctx.Admin.CreateTopicsAsync([new TopicSpecification { Name = "orders", NumPartitions = 1, ReplicationFactor = 1 }])))
            .WithSchemaRegistry(o =>
            {
                o.Migrations.AddAvro<GeneratedEvent>("orders-value");
                o.ConfigureExposedConfiguration = c => [new("Registry:Url", c.Url)];
            })
            .WithKafkaAvro()
            .WithApplication(app)
            .StartAsync(TestContext.Current.CancellationToken);
        Assert.True(app.SawSchemaAtStartup);
        await stove.Test(async t =>
        {
            await t.Kafka().Publish("orders", new GeneratedEvent { Id = "stove" });
            await t.Kafka().ShouldBePublished<GeneratedEvent>(m => m.Value.Id == "stove");
            var config = new ProducerConfig { BootstrapServers = t.Kafka().ExposedConfiguration.BootstrapServers };
            using var producer = new ProducerBuilder<string, GeneratedEvent>(config)
                .SetValueSerializer(new AvroSerializer<GeneratedEvent>(t.SchemaRegistry().Client, new AvroSerializerConfig { AutoRegisterSchemas = false })).Build();
            var headers = new Headers();
            headers.Add(StoveHeaders.TestId, System.Text.Encoding.UTF8.GetBytes(t.TestId));
            await producer.ProduceAsync("orders", new Message<string, GeneratedEvent> { Key = "native", Value = new GeneratedEvent { Id = "native" }, Headers = headers }, t.CancellationToken);
            await t.Kafka().ShouldBePublished<GeneratedEvent>(m => m.Value.Id == "native");
            Assert.Equal(2, (await t.Kafka().PeekAsync<GeneratedEvent>("orders")).Count);
        }, TestContext.Current.CancellationToken);
    }

    private sealed class RegistryStartupApplication : IApplicationUnderTest, IApplicationContext, IServiceProvider
    {
        public bool SawSchemaAtStartup { get; private set; }
        public IServiceProvider Services => this;
        public Uri BaseAddress => new("http://localhost");
        public object? GetService(Type serviceType) => null;
        public async Task<IApplicationContext> StartAsync(IReadOnlyDictionary<string, string?> configuration, CancellationToken cancellationToken)
        {
            using var client = new CachedSchemaRegistryClient(new SchemaRegistryConfig { Url = configuration["Registry:Url"] });
            var schema = await client.GetLatestSchemaAsync("orders-value").WaitAsync(cancellationToken);
            SawSchemaAtStartup = schema.SchemaString.Contains("GeneratedEvent", StringComparison.Ordinal);
            return this;
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class Certificates : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "stove-kafka-certificates-" + Guid.NewGuid().ToString("N"));
        public string CaPath => Path.Combine(_directory, "ca.pem");
        public Certificates()
        {
            Directory.CreateDirectory(_directory);
            using var caKey = RSA.Create(2048);
            var caRequest = new CertificateRequest("CN=Stove Kafka CA", caKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            caRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
            caRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign, true));
            using var ca = caRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(2));
            File.WriteAllText(CaPath, ca.ExportCertificatePem());
            Create("broker", ca, server: true);
            Create("client", ca, server: false);
        }
        private void Create(string name, X509Certificate2 ca, bool server)
        {
            using var key = RSA.Create(2048);
            var request = new CertificateRequest("CN=" + name, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new Oid(server ? "1.3.6.1.5.5.7.3.1" : "1.3.6.1.5.5.7.3.2") }, true));
            if (server)
            {
                var san = new SubjectAlternativeNameBuilder();
                san.AddDnsName("localhost");
                san.AddIpAddress(IPAddress.Loopback);
                san.AddIpAddress(IPAddress.IPv6Loopback);
                request.CertificateExtensions.Add(san.Build());
            }
            using var certificate = request.Create(ca, DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddDays(1), RandomNumberGenerator.GetBytes(16));
            File.WriteAllText(Path.Combine(_directory, name + ".pem"), certificate.ExportCertificatePem());
            File.WriteAllText(Path.Combine(_directory, name + "-key.pem"), key.ExportPkcs8PrivateKeyPem());
        }
        public void Configure(KafkaSecurityOptions security, KafkaSecurityMode mode)
        {
            security.Mode = mode;
            security.CaCertificateLocation = CaPath;
            security.BrokerCertificateLocation = Path.Combine(_directory, "broker.pem");
            security.BrokerKeyLocation = Path.Combine(_directory, "broker-key.pem");
            security.ClientCertificateLocation = Path.Combine(_directory, "client.pem");
            security.ClientKeyLocation = Path.Combine(_directory, "client-key.pem");
        }
        public void Dispose() => Directory.Delete(_directory, recursive: true);
    }
}
