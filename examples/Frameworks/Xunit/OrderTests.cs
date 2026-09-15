using System.Net;
using Npgsql;
using StoveDotnet;
using StoveDotnet.Http;
using StoveDotnet.Postgres;
using StoveDotnet.WireMock;
using Xunit;

[assembly: AssemblyFixture(typeof(FrameworkExamples.Xunit.EnvironmentFixture))]
namespace FrameworkExamples.Xunit;

public sealed class EnvironmentFixture : IAsyncLifetime
{
    public ExampleEnvironment Environment { get; } = new();
    public ValueTask InitializeAsync() => new(Environment.StartAsync(TestContext.Current.CancellationToken));
    public ValueTask DisposeAsync() { GC.SuppressFinalize(this); return Environment.DisposeAsync(); }
}

public sealed class OrderTests(EnvironmentFixture fixture)
{
    private ExampleEnvironment Environment => fixture.Environment;
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public Task Creates_order_through_real_application() => Environment.Stove.Test(async t =>
    {
        var id = Guid.NewGuid();
        var product = "chair-" + id;
        t.WireMock("catalog").MockGet("/products/" + product, responseBody: new { amount = 12.50m });
        var response = await t.Http().Post<OrderResponse>("/orders", new OrderRequest(id, product));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(12.50m, response.Body.Amount);
        var amounts = await t.Postgres().Query("select amount from orders where id = @id", r => r.GetDecimal(0), new NpgsqlParameter("id", id));
        Assert.Equal(12.50m, amounts.Single());
        await t.WireMock("catalog").ShouldHaveBeenCalled("GET", "/products/" + product);
    }, Token);

    [Fact]
    public Task Seeded_order_is_readable_through_http() => Environment.Stove.Test(async t =>
    {
        var id = Guid.NewGuid();
        await t.Postgres().Execute("insert into orders values (@id, @product, @amount)",
            new NpgsqlParameter("id", id), new NpgsqlParameter("product", "seeded"), new NpgsqlParameter("amount", 7m));
        var response = await t.Http().Get<OrderResponse>($"/orders/{id}");
        Assert.Equal("seeded", response.Body.Product);
        Assert.Equal(7m, response.Body.Amount);
    }, Token);

    [Fact]
    public async Task Concurrent_scopes_keep_data_and_stubs_separate()
    {
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var count = 0;
        await Task.WhenAll(Enumerable.Range(1, 4).Select(amount => Environment.Stove.Test(async t =>
        {
            var id = Guid.NewGuid();
            t.WireMock("catalog").MockGet("/products/shared", responseBody: new { amount });
            if (Interlocked.Increment(ref count) == 4) ready.SetResult();
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(10), t.CancellationToken);
            var response = await t.Http().Post<OrderResponse>("/orders", new OrderRequest(id, "shared"));
            Assert.Equal((decimal)amount, response.Body.Amount);
            Assert.Equal(id, response.Body.Id);
        }, Token)));
    }

    [Fact]
    public async Task Cancellation_propagates_without_failure_wrapping()
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Token);
        async Task Act() => await Environment.Stove.Test(async t =>
        {
            await cts.CancelAsync();
            await t.Postgres().Query("select 1", r => r.GetInt32(0));
        }, cts.Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(Act);
    }

    // The verification script selects this test alone to check actual runner failure/skip output and teardown.
    [Fact]
    public Task Runner_probe() => Environment.Stove.Test(async t =>
    {
        t.WireMock("catalog").MockGet("/probe", responseBody: new { ready = true });
        using var client = new HttpClient { BaseAddress = t.WireMock("catalog").ExposedConfiguration.BaseUrl };
        await client.GetStringAsync("/probe", t.CancellationToken);
        switch (System.Environment.GetEnvironmentVariable("STOVE_FRAMEWORK_PROBE"))
        {
            case "failure": Assert.Fail("intentional-framework-failure"); break;
            case "skip": Assert.Skip("intentional-framework-skip"); break;
        }
    }, Token);
}
