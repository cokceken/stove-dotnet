using System.Net;
using DotNet.Testcontainers.Builders;
using Minio;
using Minio.DataModel.Args;
using StoveDotnet;
using StoveDotnet.AspNetCore;
using StoveDotnet.Containers;
using StoveDotnet.Http;
using Xunit;

[assembly: AssemblyFixture(typeof(CustomContainer.Tests.StorageFixture))]
namespace CustomContainer.Tests;

public sealed class StorageFixture : IAsyncLifetime
{
    private const string AccessKey = "stove-example";
    private const string SecretKey = "stove-example-secret";
    private const string Bucket = "documents";
    public Stove Stove { get; private set; } = null!;

    public async ValueTask InitializeAsync() => Stove = await StoveBuilder.Create()
        .WithContainer("storage", o =>
        {
            // MinIO withdrew its official images in September 2026. This source-built mirror is pinned to the
            // final community security release by digest; users can substitute an image from their own registry.
            o.CreateContainer = () => new ContainerBuilder(
                    "ghcr.io/coollabsio/minio@sha256:7520deab26e83d3a36231bb449f86548a4b9ab2507eb388f87d1f70ef5e8c1f3")
                .WithEnvironment("MINIO_ROOT_USER", AccessKey)
                .WithEnvironment("MINIO_ROOT_PASSWORD", SecretKey)
                .WithCommand("server", "/data")
                .WithPortBinding(9000, true)
                .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r
                    .ForPort(9000).ForPath("/minio/health/ready")))
                .Build();
            o.InitializeAsync = async (container, ct) =>
            {
                using var client = new MinioClient().WithEndpoint(container.Hostname, container.GetMappedPublicPort(9000))
                    .WithCredentials(AccessKey, SecretKey).Build();
                await client.MakeBucketAsync(new MakeBucketArgs().WithBucket(Bucket), ct);
            };
            o.ConfigureExposedConfiguration = c =>
            [
                new("Storage:Endpoint", new UriBuilder("http", c.Container.Hostname, c.Container.GetMappedPublicPort(9000)).Uri.ToString()),
                new("Storage:AccessKey", AccessKey), new("Storage:SecretKey", SecretKey), new("Storage:Bucket", Bucket)
            ];
        })
        .WithHttpClient().WithAspNetCoreApplication<Program>()
        .StartAsync(TestContext.Current.CancellationToken);

    public async ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        if (Stove is not null)
            await Stove.DisposeAsync();
    }
}

public sealed class StorageTests(StorageFixture fixture)
{
    [Fact]
    public Task Uploads_and_downloads_through_the_real_api() => fixture.Stove.Test(async t =>
    {
        var key = Guid.NewGuid().ToString("N");
        (await t.Http().Put($"/objects/{key}", new { content = "contract-first testing" })).Expect(HttpStatusCode.Created);
        Assert.Equal("contract-first testing", (await t.Http().Get($"/objects/{key}")).Expect(HttpStatusCode.OK).RawBody);
        Assert.True(t.Container("storage").Container.GetMappedPublicPort(9000) > 0);
    }, TestContext.Current.CancellationToken);

    [Fact]
    public Task Missing_object_returns_not_found() => fixture.Stove.Test(async t =>
        (await t.Http().Get($"/objects/{Guid.NewGuid():N}")).Expect(HttpStatusCode.NotFound), TestContext.Current.CancellationToken);

    [Fact]
    public async Task Shared_storage_supports_overlapping_tests_with_unique_keys()
    {
        async Task Scenario(string content) => await fixture.Stove.Test(async t =>
        {
            var key = Guid.NewGuid().ToString("N");
            (await t.Http().Put($"/objects/{key}", new { content })).Expect(HttpStatusCode.Created);
            Assert.Equal(content, (await t.Http().Get($"/objects/{key}")).Expect(HttpStatusCode.OK).RawBody);
        }, TestContext.Current.CancellationToken);
        await Task.WhenAll(Scenario("one"), Scenario("two"));
    }
}
