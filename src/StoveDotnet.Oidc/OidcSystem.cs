using System.Security.Cryptography;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace StoveDotnet.Oidc;

public sealed record OidcExposedConfiguration(Uri Issuer, Uri MetadataAddress, Uri JwksUri, string Audience) : IExposedConfiguration;

public sealed class OidcOptions : SystemOptions<OidcExposedConfiguration>
{
    /// <summary>Defaults to the local server URL. With a custom issuer, configure the app's MetadataAddress separately.</summary>
    public Uri? Issuer { get; set; }
    public string Audience { get; set; } = "stove-api";
    /// <summary>Controls token issuance only, not the validator's clock.</summary>
    public TimeProvider TimeProvider { get; set; } = TimeProvider.System;
}

public enum OidcTokenSignature { PublishedKey, InvalidSignature, UnknownKey, NoSignature }

public sealed class OidcTokenOptions
{
    public string? Issuer { get; set; }
    public string? Audience { get; set; }
    public DateTimeOffset? IssuedAt { get; set; }
    public DateTimeOffset? NotBefore { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public IDictionary<string, object> Claims { get; } = new Dictionary<string, object> { ["sub"] = "stove-user" };
    public OidcTokenSignature Signature { get; set; }
}

/// <summary>A local metadata/JWKS server and token issuer, not an interactive identity provider or OAuth token endpoint.</summary>
public sealed class OidcSystem(string? name, OidcOptions options)
    : ExposingSystem<OidcOptions, OidcExposedConfiguration>(name, options), IRunAware
{
    private static readonly string[] SigningAlgorithms = ["RS256"];
    private static readonly string[] ResponseTypes = ["id_token"];
    private static readonly string[] SubjectTypes = ["public"];
    private readonly object _gate = new();
    private RSA? _rsa;
    private RSA? _foreign;
    private WebApplication? _server;
    private bool _disposed;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Options.Issuer is { } configured && (!configured.IsAbsoluteUri || configured.Scheme is not ("http" or "https")))
            throw new ArgumentException("Issuer must be an absolute HTTP(S) URI.");
        ArgumentException.ThrowIfNullOrWhiteSpace(Options.Audience);
        ArgumentNullException.ThrowIfNull(Options.TimeProvider);
        _rsa = RSA.Create(2048);
        _foreign = RSA.Create(2048);
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");
        _server = builder.Build();
        var parameters = _rsa.ExportParameters(false);
        _server.MapGet("/.well-known/openid-configuration", () => Results.Json(new
        {
            issuer = ExposedConfiguration.Issuer.ToString(),
            jwks_uri = ExposedConfiguration.JwksUri.ToString(),
            id_token_signing_alg_values_supported = SigningAlgorithms,
            response_types_supported = ResponseTypes,
            subject_types_supported = SubjectTypes
        }));
        _server.MapGet("/jwks", () => Results.Json(new
        {
            keys = new[] { new { kty = "RSA", use = "sig", alg = "RS256", kid = "stove-key",
                n = Base64UrlEncoder.Encode(parameters.Modulus), e = Base64UrlEncoder.Encode(parameters.Exponent) } }
        }));
        await _server.StartAsync(cancellationToken).ConfigureAwait(false);
        var address = new Uri(_server.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single());
        Expose(new(Options.Issuer ?? address, new Uri(address, "/.well-known/openid-configuration"), new Uri(address, "/jwks"), Options.Audience));
    }

    /// <summary>Issues a token without storing it in diagnostics. Token overrides are local to this call.</summary>
    public string IssueToken(Action<OidcTokenOptions>? configure = null)
    {
        var token = new OidcTokenOptions();
        configure?.Invoke(token);
        foreach (var key in new[] { "iss", "aud", "iat", "nbf", "exp" })
            if (token.Claims.ContainsKey(key)) throw new ArgumentException($"Use token options instead of the reserved '{key}' claim.");
        if (!Enum.IsDefined(token.Signature)) throw new ArgumentOutOfRangeException(nameof(configure));
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var exposed = ExposedConfiguration;
            var now = Options.TimeProvider.GetUtcNow();
            var issued = token.IssuedAt ?? now;
            var key = new RsaSecurityKey(token.Signature == OidcTokenSignature.PublishedKey ? _rsa! : _foreign!)
                { KeyId = token.Signature == OidcTokenSignature.UnknownKey ? "unknown-key" : "stove-key" };
            return new JsonWebTokenHandler { SetDefaultTimesOnTokenCreation = false }.CreateToken(new SecurityTokenDescriptor
            {
                Issuer = token.Issuer ?? exposed.Issuer.ToString(), Audience = token.Audience ?? exposed.Audience,
                IssuedAt = issued.UtcDateTime, NotBefore = (token.NotBefore ?? issued).UtcDateTime,
                Expires = (token.ExpiresAt ?? issued.AddMinutes(5)).UtcDateTime,
                Claims = new Dictionary<string, object>(token.Claims),
                SigningCredentials = token.Signature == OidcTokenSignature.NoSignature ? null : new SigningCredentials(key, SecurityAlgorithms.RsaSha256)
            });
        }
    }

    public override async ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _rsa?.Dispose();
            _foreign?.Dispose();
        }
        if (_server is not null) await _server.DisposeAsync().ConfigureAwait(false);
    }
}

public static class OidcStoveExtensions
{
    public static StoveBuilder WithOidc(this StoveBuilder builder, Action<OidcOptions>? configure = null) => builder.WithOidc(null, configure);
    public static StoveBuilder WithOidc(this StoveBuilder builder, string? name, Action<OidcOptions>? configure = null)
    {
        var options = new OidcOptions();
        configure?.Invoke(options);
        return builder.WithSystem(new OidcSystem(name, options));
    }
    public static OidcSystem Oidc(this StoveTestContext test, string? name = null) => test.GetSystem<OidcSystem>(name);
}
