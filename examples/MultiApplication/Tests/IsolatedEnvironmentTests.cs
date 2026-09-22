using System.Net;
using StoveDotnet;
using StoveDotnet.Http;
using Xunit;

namespace MultiApplication.Tests;

public sealed class IsolatedEnvironmentTests
{
    [Fact]
    public async Task Independent_databases_brokers_and_hosts_can_reuse_the_same_business_key()
    {
        var id = Guid.NewGuid();
        async Task Scenario(string product)
        {
            // Each environment owns containers and hosts. No transaction around HTTP can provide this isolation.
            await using var stove = await EnvironmentFixture.Build().StartAsync(TestContext.Current.CancellationToken);
            await stove.Test(async t =>
            {
                (await t.Http("api").Post("/orders", new { id, product })).Expect(HttpStatusCode.Accepted);
                var result = await Eventually.UntilAsync<Order?>(async ct =>
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, $"/orders/{id}");
                    var response = await t.Http("api").Send<Order>(request, ct);
                    return response.StatusCode == HttpStatusCode.NotFound ? null : response.Expect(HttpStatusCode.OK).Body;
                }, value => value is not null, TimeSpan.FromSeconds(10), cancellationToken: t.CancellationToken);
                Assert.Equal(product, result!.Product);
                // Probe one specific absent key; unrelated work may run on other keys.
                var absent = Guid.NewGuid();
                await Eventually.ThroughoutAsync(async ct =>
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, $"/orders/{absent}");
                    (await t.Http("api").Send(request, ct)).Expect(HttpStatusCode.NotFound);
                }, TimeSpan.FromMilliseconds(300), cancellationToken: t.CancellationToken);
            }, TestContext.Current.CancellationToken);
        }
        await Task.WhenAll(Scenario("chair"), Scenario("table"));
    }

    private sealed record Order(Guid Id, string Product);
}
