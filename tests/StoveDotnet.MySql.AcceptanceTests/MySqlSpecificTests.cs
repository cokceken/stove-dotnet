using System.Net;
using MySqlConnector;
using StoveDotnet.Http;
using StoveDotnet.Testing;
using Xunit;

namespace StoveDotnet.MySql.AcceptanceTests;

[Collection("MySql")]
public sealed class MySqlSpecificTests(MySqlFixture fixture)
{
    [Fact]
    public Task Unicode_round_trips_between_native_driver_and_application() => fixture.Stove.Test(async t =>
    {
        var id = Guid.NewGuid().ToString();
        const string value = "İstanbul 日本語 🐬";
        await fixture.Module.Seed(t, id, value);
        var response = await t.Http().Get<RecordRequest>($"/records/{id}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(value, response.Body.Value);
        var secondId = Guid.NewGuid().ToString();
        Assert.Equal(HttpStatusCode.Created, (await t.Http().Post("/records", new RecordRequest(secondId, value))).StatusCode);
        Assert.Equal([value], await fixture.Module.Read(t, secondId));
        Assert.Equal(["stove"], await t.MySql("Primary").Query("select database()", r => r.GetString(0)));
    }, TestContext.Current.CancellationToken);

    [Fact]
    public async Task Cancellation_inside_a_scope_reaches_the_driver()
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        await fixture.Stove.Test(async t =>
        {
            await cts.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => t.MySql("Primary").Execute("select @value", new MySqlParameter("value", 1)));
        }, cts.Token);
    }
}
