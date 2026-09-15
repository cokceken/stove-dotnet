using System.Net;
using Npgsql;
using StoveDotnet;
using StoveDotnet.Http;
using StoveDotnet.Postgres;
using StoveDotnet.WireMock;
using Microsoft.VisualStudio.TestTools.UnitTesting;

[assembly: Parallelize(Scope = ExecutionScope.MethodLevel)]
namespace FrameworkExamples.MSTest;

[TestClass]
public sealed class EnvironmentFixture
{
    public static ExampleEnvironment Environment { get; } = new();
    [AssemblyInitialize]
    public static Task Start(TestContext context) => Environment.StartAsync(context.CancellationToken);
    [AssemblyCleanup]
    public static async Task Stop() => await Environment.DisposeAsync();
}

[TestClass]
public sealed class OrderTests
{
    public TestContext TestContext { get; set; } = null!;
    private static ExampleEnvironment Environment => EnvironmentFixture.Environment;
    private CancellationToken Token => TestContext.CancellationToken;

    [TestMethod]
    public Task Creates_order_through_real_application() => Environment.Stove.Test(async t =>
    {
        var id = Guid.NewGuid();
        var product = "chair-" + id;
        t.WireMock("catalog").MockGet("/products/" + product, responseBody: new { amount = 12.50m });
        var response = await t.Http().Post<OrderResponse>("/orders", new OrderRequest(id, product));
        Assert.AreEqual(HttpStatusCode.Created, response.StatusCode);
        Assert.AreEqual(12.50m, response.Body.Amount);
        var amounts = await t.Postgres().Query("select amount from orders where id = @id", r => r.GetDecimal(0), new NpgsqlParameter("id", id));
        Assert.AreEqual(12.50m, amounts.Single());
        await t.WireMock("catalog").ShouldHaveBeenCalled("GET", "/products/" + product);
    }, Token);

    [TestMethod]
    public Task Seeded_order_is_readable_through_http() => Environment.Stove.Test(async t =>
    {
        var id = Guid.NewGuid();
        await t.Postgres().Execute("insert into orders values (@id, @product, @amount)",
            new NpgsqlParameter("id", id), new NpgsqlParameter("product", "seeded"), new NpgsqlParameter("amount", 7m));
        var response = await t.Http().Get<OrderResponse>($"/orders/{id}");
        Assert.AreEqual("seeded", response.Body.Product);
        Assert.AreEqual(7m, response.Body.Amount);
    }, Token);

    [TestMethod]
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
            Assert.AreEqual((decimal)amount, response.Body.Amount);
            Assert.AreEqual(id, response.Body.Id);
        }, Token)));
    }

    [TestMethod]
    public async Task Cancellation_propagates_without_failure_wrapping()
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Token);
        async Task Act() => await Environment.Stove.Test(async t =>
        {
            await cts.CancelAsync();
            await t.Postgres().Query("select 1", r => r.GetInt32(0));
        }, cts.Token);
        await Assert.ThrowsAsync<OperationCanceledException>(Act);
    }

    // The verification script selects this test alone to check actual runner failure/skip output and teardown.
    [TestMethod]
    public Task Runner_probe() => Environment.Stove.Test(async t =>
    {
        t.WireMock("catalog").MockGet("/probe", responseBody: new { ready = true });
        using var client = new HttpClient { BaseAddress = t.WireMock("catalog").ExposedConfiguration.BaseUrl };
        await client.GetStringAsync("/probe", t.CancellationToken);
        switch (System.Environment.GetEnvironmentVariable("STOVE_FRAMEWORK_PROBE"))
        {
            case "failure": Assert.Fail("intentional-framework-failure"); break;
            case "skip": Assert.Inconclusive("intentional-framework-skip"); break;
        }
    }, Token);
}
