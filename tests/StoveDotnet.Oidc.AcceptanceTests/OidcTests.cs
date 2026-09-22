using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using StoveDotnet.AspNetCore;
using StoveDotnet.Http;
using StoveDotnet.Oidc;
using Xunit;

[assembly: AssemblyFixture(typeof(StoveDotnet.Oidc.AcceptanceTests.OidcFixture))]
namespace StoveDotnet.Oidc.AcceptanceTests;

public sealed class OidcFixture : IAsyncLifetime
{
    public Stove Stove { get; private set; } = null!;
    public async ValueTask InitializeAsync() => Stove = await Build().StartAsync(TestContext.Current.CancellationToken);
    internal static StoveBuilder Build(Uri? issuer = null, TimeProvider? clock = null) => StoveBuilder.Create().WithOidc("identity", o =>
    {
        o.Issuer = issuer;
        o.Audience = "acceptance-api";
        o.TimeProvider = clock ?? TimeProvider.System;
        o.ConfigureExposedConfiguration = c => [new("Auth:Issuer", c.Issuer.ToString()), new("Auth:Metadata", c.MetadataAddress.ToString()),
            new("Auth:Audience", c.Audience), new("Auth:RequireHttpsMetadata", "false")];
    }).WithHttpClient().WithAspNetCoreApplication<Program>();
    public async ValueTask DisposeAsync() { GC.SuppressFinalize(this); await Stove.DisposeAsync(); }
}

public sealed class OidcTests(OidcFixture fixture)
{
    [Fact]
    public async Task Issuance_clock_does_not_advance_the_bearer_validators_clock()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow.AddHours(1));
        await using var stove = await OidcFixture.Build(clock: clock).StartAsync(TestContext.Current.CancellationToken);
        await stove.Test(async t =>
            (await t.Http().Get("/me", Bearer(t.Oidc().IssueToken()))).Expect(HttpStatusCode.Unauthorized), TestContext.Current.CancellationToken);
    }
    [Fact]
    public Task Real_bearer_pipeline_loads_metadata_and_validates_claims() => fixture.Stove.Test(async t =>
    {
        var token = t.Oidc().IssueToken(o => { o.Claims["sub"] = "alice"; o.Claims["permission"] = "write"; });
        var headers = Bearer(token);
        Assert.Equal("alice", (await t.Http().Get<User>("/me", headers)).Expect(HttpStatusCode.OK).Body.Subject);
        (await t.Http().Post("/write", headers: headers)).Expect(HttpStatusCode.NoContent);
        using var client = new HttpClient();
        var discovery = await client.GetStringAsync(t.Oidc().ExposedConfiguration.MetadataAddress, t.CancellationToken);
        Assert.Contains(t.Oidc().ExposedConfiguration.Issuer.ToString(), discovery, StringComparison.Ordinal);
        var jwks = await client.GetStringAsync(t.Oidc().ExposedConfiguration.JwksUri, t.CancellationToken);
        using var json = JsonDocument.Parse(jwks);
        var key = json.RootElement.GetProperty("keys")[0];
        Assert.Equal("RSA", key.GetProperty("kty").GetString());
        Assert.False(key.TryGetProperty("d", out _));
    }, TestContext.Current.CancellationToken);

    [Theory]
    [InlineData("missing")]
    [InlineData("malformed")]
    [InlineData("issuer")]
    [InlineData("audience")]
    [InlineData("expired")]
    [InlineData("future")]
    [InlineData("signature")]
    [InlineData("unknown")]
    [InlineData("none")]
    public Task Invalid_tokens_are_unauthorized(string scenario) => fixture.Stove.Test(async t =>
    {
        var token = t.Oidc().IssueToken(o =>
        {
            switch (scenario)
            {
                case "issuer": o.Issuer = "https://wrong.example"; break;
                case "audience": o.Audience = "wrong-api"; break;
                case "expired": o.IssuedAt = DateTimeOffset.UtcNow.AddHours(-2); o.ExpiresAt = DateTimeOffset.UtcNow.AddHours(-1); break;
                case "future": o.IssuedAt = DateTimeOffset.UtcNow.AddHours(1); break;
                case "signature": o.Signature = OidcTokenSignature.InvalidSignature; break;
                case "unknown": o.Signature = OidcTokenSignature.UnknownKey; break;
                case "none": o.Signature = OidcTokenSignature.NoSignature; break;
            }
        });
        (await t.Http().Get("/me", scenario == "missing" ? null : Bearer(scenario == "malformed" ? "not-a-jwt" : token))).Expect(HttpStatusCode.Unauthorized);
    }, TestContext.Current.CancellationToken);

    [Fact]
    public Task Valid_token_without_required_permission_is_forbidden() => fixture.Stove.Test(async t =>
        (await t.Http().Post("/write", headers: Bearer(t.Oidc().IssueToken()))).Expect(HttpStatusCode.Forbidden), TestContext.Current.CancellationToken);

    [Fact]
    public async Task Custom_issuer_uses_explicit_local_metadata_and_server_is_disposed()
    {
        await using var stove = await OidcFixture.Build(new Uri("https://issuer.example/custom")).StartAsync(TestContext.Current.CancellationToken);
        Uri? metadata = null;
        await stove.Test(async t =>
        {
            metadata = t.Oidc().ExposedConfiguration.MetadataAddress;
            (await t.Http().Get("/me", Bearer(t.Oidc().IssueToken()))).Expect(HttpStatusCode.OK);
        }, TestContext.Current.CancellationToken);
        await stove.DisposeAsync();
        using var client = new HttpClient();
        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetStringAsync(metadata, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Named_issuers_have_independent_keys_and_concurrent_claims()
    {
        await using var stove = await StoveBuilder.Create().WithOidc("one").WithOidc("two").StartAsync(TestContext.Current.CancellationToken);
        await stove.Test(async t =>
        {
            Assert.Throws<InvalidOperationException>(() => t.Oidc());
            var tokens = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => Task.Run(() => t.Oidc("one").IssueToken(o => o.Claims["sub"] = i.ToString(System.Globalization.CultureInfo.InvariantCulture)))));
            Assert.Equal(8, tokens.Distinct().Count());
            using var client = new HttpClient();
            Assert.NotEqual(await client.GetStringAsync(t.Oidc("one").ExposedConfiguration.JwksUri, t.CancellationToken),
                await client.GetStringAsync(t.Oidc("two").ExposedConfiguration.JwksUri, t.CancellationToken));
        }, TestContext.Current.CancellationToken);
    }

    private static Dictionary<string, string> Bearer(string token) => new() { ["Authorization"] = "Bearer " + token };
    private sealed record User(string Subject);
}
