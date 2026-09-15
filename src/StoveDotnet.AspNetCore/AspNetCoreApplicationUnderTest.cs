using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using StoveDotnet.Hosting;

namespace StoveDotnet.AspNetCore;

public sealed class AspNetCoreApplicationOptions : ApplicationOptions
{
    /// <summary>Kestrel port; 0 picks a free port.</summary>
    public int Port { get; set; }

    /// <summary>Hosting environment name. Defaults to WebApplicationFactory's default (Development).</summary>
    public string? Environment { get; set; }

    /// <summary>
    /// Extra web host customization. Anything done here (e.g. replacing services) is the test author's decision; Stove
    /// itself never replaces application services.
    /// </summary>
    public Action<IWebHostBuilder>? ConfigureWebHost { get; set; }
}

/// <summary>Runs an ASP.NET Core application in-process on a real Kestrel port.</summary>
public sealed class AspNetCoreApplicationUnderTest<TEntryPoint> : IApplicationUnderTest, IFailureDetailsProvider where TEntryPoint : class
{
    private readonly ApplicationLogCollector _logs = new();
    private readonly AspNetCoreApplicationOptions _options;
    private StoveWebApplicationFactory? _factory;

    public AspNetCoreApplicationUnderTest(AspNetCoreApplicationOptions options)
    {
        _options = options;
    }

    public async Task<IApplicationContext> StartAsync(IReadOnlyDictionary<string, string?> configuration, CancellationToken cancellationToken)
    {
        _factory = new StoveWebApplicationFactory(configuration, _options, _logs);
        _factory.UseKestrel(_options.Port);
        await Task.Run(_factory.StartServer, cancellationToken).ConfigureAwait(false);

        var addresses = _factory.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses;
        var address = addresses?.FirstOrDefault()
            ?? throw new InvalidOperationException("The application started but Kestrel reported no listening address.");

        return new AspNetCoreApplicationContext(_factory.Services, new Uri(address.Replace("[::]", "localhost", StringComparison.Ordinal)));
    }

    public async ValueTask DisposeAsync()
    {
        if (_factory is not null)
        {
            await _factory.DisposeAsync().ConfigureAwait(false);
        }
    }

    public Task<FailureDetails?> DescribeAsync(StoveTestContext test, CancellationToken cancellationToken) => _logs.DescribeAsync(test, cancellationToken);

    private sealed class StoveWebApplicationFactory(
        IReadOnlyDictionary<string, string?> configuration,
        AspNetCoreApplicationOptions options, ApplicationLogCollector logs) : WebApplicationFactory<TEntryPoint>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            if (options.Environment is not null)
            {
                builder.UseEnvironment(options.Environment);
            }

            // UseSetting values are visible to top-level Program.cs code before Build().
            foreach (var (key, value) in configuration)
            {
                builder.UseSetting(key, value);
            }

            options.ConfigureWebHost?.Invoke(builder);
            builder.ConfigureLogging(logging => logging.AddProvider(logs));
        }
    }

    private sealed record AspNetCoreApplicationContext(IServiceProvider Services, Uri BaseAddress) : IApplicationContext;
}

public static class AspNetCoreStoveBuilderExtensions
{
    /// <summary>Runs <typeparamref name="TEntryPoint"/>'s application (usually <c>Program</c>) on a real Kestrel port.</summary>
    public static StoveBuilder WithAspNetCoreApplication<TEntryPoint>(
        this StoveBuilder builder,
        Action<AspNetCoreApplicationOptions>? configure = null) where TEntryPoint : class
        => builder.WithAspNetCoreApplication<TEntryPoint>(null, configure);

    public static StoveBuilder WithAspNetCoreApplication<TEntryPoint>(
        this StoveBuilder builder, string? name,
        Action<AspNetCoreApplicationOptions>? configure = null) where TEntryPoint : class
    {
        var options = new AspNetCoreApplicationOptions();
        configure?.Invoke(options);
        return builder.WithApplication(name, new AspNetCoreApplicationUnderTest<TEntryPoint>(options), options);
    }
}
