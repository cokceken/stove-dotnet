using System.Net;
using StoveDotnet.AspNetCore;
using StoveDotnet.Http;
using StoveDotnet.Telemetry;
using StoveDotnet.WireMock;
using Xunit;

[assembly: AssemblyFixture(typeof(StoveDotnet.UnitTests.InProcessStoveFixture))]

namespace StoveDotnet.UnitTests;

/// <summary>A Stove environment that needs no container runtime: telemetry, WireMock, HTTP and the test app.</summary>
public sealed class InProcessStoveFixture : IAsyncLifetime
{
    public Stove Stove { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        Stove = await StoveBuilder.Create()
            .WithTelemetry()
            .WithWireMock("downstream", o => o.ConfigureExposedConfiguration = c => [new("Downstream:BaseUrl", c.BaseUrl.ToString())])
            .WithWireMock("unused")
            .WithSystem(new StaticConfiguration([new("TestApp:Greeting", "hello from stove")]))
            .WithHttpClient()
            .WithAspNetCoreApplication<Program>()
            .StartAsync();
    }

    public async ValueTask DisposeAsync() => await Stove.DisposeAsync();

    private sealed class StaticConfiguration(IEnumerable<KeyValuePair<string, string?>> configuration) : IPluggedSystem, IExposesConfiguration
    {
        public string? Name => null;

        public IEnumerable<KeyValuePair<string, string?>> Configuration() => configuration;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

public sealed class InProcessStoveTests(InProcessStoveFixture fixture)
{
    private readonly Stove _stove = fixture.Stove;

    [Fact]
    public Task Configuration_reaches_program_startup() => _stove.Test(async t =>
    {
        var response = await t.Http().Get<Greeting>("/greeting");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("hello from stove", response.Body.Message);
        Assert.Equal("http", t.Application.BaseAddress.Scheme);
    });

    [Fact]
    public Task Posts_json_and_resolves_app_services() => _stove.Test(async t =>
    {
        var response = await t.Http().Post<Greeting>("/echo", new Greeting("hi"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("hi", response.Body.Message);
        await t.Using<GreetingService>(service =>
        {
            Assert.Equal("hello from stove", service.Message);
            return Task.CompletedTask;
        });
    });

    [Fact]
    public Task Named_wiremock_stubs_and_verifies_calls_of_this_test() => _stove.Test(async t =>
    {
        var downstream = t.WireMock("downstream");
        downstream.MockGet("/items/42", responseBody: new { id = 42, name = "chair" });

        var response = await t.Http().Get("/downstream/42");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("chair", response.RawBody, StringComparison.Ordinal);
        await downstream.ShouldHaveBeenCalled("GET", "/items/42");
        await downstream.ShouldNotHaveBeenCalled("GET", "/items/43");
    });

    [Fact]
    public async Task Stubs_are_removed_when_the_test_ends()
    {
        Uri? stubUrl = null;
        await _stove.Test(async t =>
        {
            var downstream = t.WireMock("downstream");
            downstream.MockGet("/only-in-this-test", statusCode: 204);
            stubUrl = new Uri(downstream.ExposedConfiguration.BaseUrl, "/only-in-this-test");

            using var client = new HttpClient();
            Assert.Equal(HttpStatusCode.NoContent, (await client.GetAsync(stubUrl, t.CancellationToken)).StatusCode);
        });

        using var client = new HttpClient();
        var afterTest = await client.GetAsync(stubUrl, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, afterTest.StatusCode);
    }

    [Fact]
    public Task Captures_application_spans_for_the_test() => _stove.Test(async t =>
    {
        await t.Http().Get("/greeting");

        var span = await t.Telemetry().ShouldContainSpan(s => s.Kind == "Server" && s.Attribute("url.path") as string == "/greeting");

        Assert.Equal(t.TraceId, span.TraceId);
        Assert.Equal("test-app", span.ServiceName);
    });

    [Fact]
    public async Task Failed_test_includes_trace_tree_and_logs()
    {
        var error = await Assert.ThrowsAsync<StoveTestFailedException>(() => _stove.Test(async t =>
        {
            var response = await t.Http().Get("/boom");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }));

        Assert.IsType<Xunit.Sdk.EqualException>(error.InnerException);
        Assert.Contains("--- Stove: telemetry ---", error.Message, StringComparison.Ordinal);
        Assert.Contains("[x] GET /boom", error.Message, StringComparison.Ordinal);
        Assert.Contains("About to fail", error.Message, StringComparison.Ordinal);
    }
}
