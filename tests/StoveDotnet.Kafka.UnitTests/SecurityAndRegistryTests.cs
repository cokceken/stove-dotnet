using Confluent.Kafka;
using StoveDotnet.SchemaRegistry;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;
using Xunit;

namespace StoveDotnet.Kafka.UnitTests;

public sealed class SecurityAndRegistryTests
{
    [Theory]
    [InlineData(KafkaSecurityMode.Plaintext, SecurityProtocol.Plaintext)]
    [InlineData(KafkaSecurityMode.Tls, SecurityProtocol.Ssl)]
    [InlineData(KafkaSecurityMode.MutualTls, SecurityProtocol.Ssl)]
    public void Explicit_transport_keeps_certificate_and_hostname_verification(KafkaSecurityMode mode, SecurityProtocol protocol)
    {
        var security = new KafkaSecurityOptions
        {
            Mode = mode, CaCertificateLocation = "ca.pem", ClientCertificateLocation = "client.pem", ClientKeyLocation = "key.pem"
        };
        var config = new ClientConfig();
        security.ApplyTo(config);
        Assert.Equal(protocol, config.SecurityProtocol);
        if (mode != KafkaSecurityMode.Plaintext)
        {
            Assert.True(config.EnableSslCertificateVerification);
            Assert.Equal(SslEndpointIdentificationAlgorithm.Https, config.SslEndpointIdentificationAlgorithm);
            Assert.Equal("ca.pem", config.SslCaLocation);
        }
        Assert.Equal(mode == KafkaSecurityMode.MutualTls ? "client.pem" : null, config.SslCertificateLocation);
    }

    [Theory]
    [InlineData(KafkaSecurityMode.Tls)]
    [InlineData(KafkaSecurityMode.MutualTls)]
    public async Task Missing_certificates_fail_before_container_start(KafkaSecurityMode mode)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => StoveBuilder.Create().WithKafka(o => o.Security.Mode = mode).StartAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Existing_registry_supports_migration_skip_and_cleanup_without_removing_endpoint()
    {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/subjects").UsingGet()).RespondWith(Response.Create().WithStatusCode(200).WithBody("[]"));
        var migrations = 0;
        var cleanups = 0;
        var options = new SchemaRegistryOptions();
        options.UseExisting(server.Url!, runMigrations: false);
        options.Migrations.Add((_, _) => { migrations++; return Task.CompletedTask; });
        options.Cleanup = (_, _) => { cleanups++; return Task.CompletedTask; };
        await using var kafka = new KafkaSystem(null, new KafkaOptions());
        var registry = new SchemaRegistrySystem(null, options, kafka);
        await registry.RunAsync(TestContext.Current.CancellationToken);
        Assert.Equal(server.Url, registry.ExposedConfiguration.Url);
        Assert.Equal(0, migrations);
        await registry.DisposeAsync();
        await registry.DisposeAsync();
        Assert.Equal(1, cleanups);
        using var http = new HttpClient();
        Assert.Equal("[]", await http.GetStringAsync(server.Url + "/subjects", TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("not-a-url")]
    [InlineData("file:///registry")]
    public void Existing_registry_requires_http_url(string url) => Assert.Throws<ArgumentException>(() => new SchemaRegistryOptions().UseExisting(url));
}
