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

/// <summary>Addresses of the local identity provider and the audience its tokens carry.</summary>
/// <param name="Issuer">The <c>iss</c> value of issued tokens.</param>
/// <param name="MetadataAddress">The OpenID Connect discovery document; point the application's JWT bearer
/// <c>MetadataAddress</c> or <c>Authority</c> here.</param>
/// <param name="JwksUri">The published signing keys.</param>
/// <param name="Audience">The <c>aud</c> value of issued tokens.</param>
public sealed record OidcExposedConfiguration(Uri Issuer, Uri MetadataAddress, Uri JwksUri, string Audience) : IExposedConfiguration;

/// <summary>Options of the local OpenID Connect metadata server and token issuer.</summary>
public sealed class OidcOptions : SystemOptions<OidcExposedConfiguration>
{
    /// <summary>Defaults to the local server URL. With a custom issuer, configure the app's MetadataAddress separately.</summary>
    public Uri? Issuer { get; set; }
    /// <summary>
    /// The <c>aud</c> claim of issued tokens unless a token overrides it. Defaults to <c>stove-api</c>.
    /// </summary>
    public string Audience { get; set; } = "stove-api";
    /// <summary>Controls token issuance only, not the validator's clock.</summary>
    public TimeProvider TimeProvider { get; set; } = TimeProvider.System;
}

/// <summary>How an issued token is signed, to exercise the application's signature validation.</summary>
public enum OidcTokenSignature
{
    /// <summary>Signed with the key published at the JWKS endpoint. The application accepts it.</summary>
    PublishedKey,

    /// <summary>Carries the published key id but is signed with another key, so the signature does not verify.</summary>
    InvalidSignature,

    /// <summary>Signed with a key whose id is not published.</summary>
    UnknownKey,

    /// <summary>Not signed at all.</summary>
    NoSignature,
}

/// <summary>
/// Per-token overrides for <see cref="OidcSystem.IssueToken"/>. Everything not set comes from the system's options.
/// </summary>
public sealed class OidcTokenOptions
{
    /// <summary>Overrides the <c>iss</c> claim, for example to test issuer validation.</summary>
    public string? Issuer { get; set; }
    /// <summary>Overrides the <c>aud</c> claim, for example to test audience validation.</summary>
    public string? Audience { get; set; }
    /// <summary>Overrides the <c>iat</c> claim. Defaults to the issuer's current time.</summary>
    public DateTimeOffset? IssuedAt { get; set; }
    /// <summary>Overrides the <c>nbf</c> claim. Defaults to the issue time.</summary>
    public DateTimeOffset? NotBefore { get; set; }
    /// <summary>Overrides the <c>exp</c> claim. Defaults to five minutes after the issue time.</summary>
    public DateTimeOffset? ExpiresAt { get; set; }
    /// <summary>
    /// Claims of the token. Starts with <c>sub = stove-user</c>; add roles, scopes or anything the application
    /// reads.
    /// </summary>
    public IDictionary<string, object> Claims { get; } = new Dictionary<string, object> { ["sub"] = "stove-user" };
    /// <summary>How the token is signed. Defaults to the published key, which the application accepts.</summary>
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

    /// <inheritdoc />
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

    /// <inheritdoc />
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

/// <summary>Registers the module on a <see cref="StoveBuilder"/> and resolves it from a test context.</summary>
public static class OidcStoveExtensions
{
    /// <summary>Registers the default (unnamed) instance.</summary>
    public static StoveBuilder WithOidc(this StoveBuilder builder, Action<OidcOptions>? configure = null) => builder.WithOidc(null, configure);
    /// <summary>
    /// Registers a named instance, so several of the same kind can run side by side. Resolve it in a test by
    /// passing the same name to the accessor.
    /// </summary>
    public static StoveBuilder WithOidc(this StoveBuilder builder, string? name, Action<OidcOptions>? configure = null)
    {
        var options = new OidcOptions();
        configure?.Invoke(options);
        return builder.WithSystem(new OidcSystem(name, options));
    }
    /// <summary>
    /// The system registered on the running environment: the instance with the given name; without a name, the
    /// unnamed instance or the only one registered.
    /// </summary>
    public static OidcSystem Oidc(this StoveTestContext test, string? name = null) => test.GetSystem<OidcSystem>(name);
}
