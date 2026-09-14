using System.Net;
using System.Net.Http.Json;
using System.Text;
using StoveDotnet.WireMock;
using Xunit;

namespace StoveDotnet.Hosting.AcceptanceTests;

public sealed class WireMockHelperTests(InProcessStoveFixture fixture)
{
    private readonly Stove _stove = fixture.Stove;

    [Theory]
    [InlineData("/stock/{productId}", "/stock/chair", true)]
    [InlineData("/stock/{productId}", "/STOCK/chair", true)]
    [InlineData("/stock/{productId}", "/stock/chair/extra", false)]
    [InlineData("/{bucket}/{key+}", "/invoices/2026/09/inv-1.pdf", true)]
    [InlineData("/charges", "/charges", true)]
    [InlineData("/charges", "/charges/1", false)]
    [InlineData("/v1.0/items(1)", "/v1.0/items(1)", true)]
    public void Path_templates_match_like_openapi(string template, string path, bool expected) =>
        Assert.Equal(expected, PathTemplate.Parse(template).IsMatch(path));

    [Fact]
    public void Path_templates_extract_parameters()
    {
        Assert.True(PathTemplate.Parse("/{bucket}/{object-key+}").TryMatch("/invoices/2026/inv%201.pdf", out var parameters));

        Assert.Equal("invoices", parameters["bucket"]);
        Assert.Equal("2026/inv 1.pdf", parameters["object-key"]);
    }

    [Fact]
    public Task Templated_stubs_record_requests_with_parameters_and_bodies() => _stove.Test(async t =>
    {
        var wireMock = t.WireMock("downstream");
        wireMock.MockPost("/accounts/{accountId}/charges", statusCode: 201, responseBody: new { id = "c-1" });
        using var client = new HttpClient { BaseAddress = wireMock.ExposedConfiguration.BaseUrl };

        var response = await client.PostAsJsonAsync("/accounts/acc-7/charges?currency=EUR", new Charge("order-1", 20m), t.CancellationToken);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var request = Assert.Single(await wireMock.ShouldHaveBeenCalled("POST", "/accounts/{accountId}/charges"));
        Assert.Equal("acc-7", request.PathParameters["accountId"]);
        Assert.Equal("EUR", request.QueryValue("currency"));
        Assert.Equal(new Charge("order-1", 20m), request.BodyAs<Charge>());
        Assert.Empty(wireMock.Requests("GET"));
        await Assert.ThrowsAsync<StoveAssertionException>(() => wireMock.ShouldHaveBeenCalled("POST", "/accounts/{accountId}/charges", times: 2));
    });

    [Fact]
    public Task Raw_responses_serve_xml_and_binary_bodies() => _stove.Test(async t =>
    {
        var wireMock = t.WireMock("downstream");
        wireMock.MockRaw("PUT", "/{bucket}/{key+}", 200, "<PutObjectResult/>", "application/xml", new Dictionary<string, string> { ["ETag"] = "\"abc\"" });
        wireMock.MockRaw("GET", "/{bucket}/{key+}", 200, [1, 2, 3], "application/pdf");
        using var client = new HttpClient { BaseAddress = wireMock.ExposedConfiguration.BaseUrl };

        using var put = await client.PutAsync("/invoices/2026/inv-1.pdf", new ByteArrayContent(Encoding.UTF8.GetBytes("pdf")), t.CancellationToken);
        var download = await client.GetByteArrayAsync("/invoices/2026/inv-1.pdf", t.CancellationToken);

        Assert.Equal("<PutObjectResult/>", await put.Content.ReadAsStringAsync(t.CancellationToken));
        Assert.Equal("\"abc\"", put.Headers.ETag?.Tag);
        Assert.Equal([1, 2, 3], download);
        var upload = Assert.Single(await wireMock.ShouldHaveBeenCalled("PUT", "/{bucket}/{key+}"));
        Assert.Equal("2026/inv-1.pdf", upload.PathParameters["key"]);
    });

    [Fact]
    public async Task Requests_of_other_tests_are_not_returned()
    {
        var foreignTestId = string.Empty;
        await _stove.Test(async t =>
        {
            var wireMock = t.WireMock("downstream");
            wireMock.Server.Given(global::WireMock.RequestBuilders.Request.Create().WithPath("/shared").UsingGet())
                .RespondWith(global::WireMock.ResponseBuilders.Response.Create().WithStatusCode(204));
            using var client = new HttpClient { BaseAddress = wireMock.ExposedConfiguration.BaseUrl };
            using var request = new HttpRequestMessage(HttpMethod.Get, "/shared");
            request.Headers.Add(StoveHeaders.TestId, t.TestId);
            foreignTestId = t.TestId;
            await client.SendAsync(request, t.CancellationToken);
        });

        await _stove.Test(async t =>
        {
            Assert.NotEqual(foreignTestId, t.TestId);
            await t.WireMock("downstream").ShouldNotHaveBeenCalled("GET", "/shared");
        });
    }

    private sealed record Charge(string OrderId, decimal Amount);
}
