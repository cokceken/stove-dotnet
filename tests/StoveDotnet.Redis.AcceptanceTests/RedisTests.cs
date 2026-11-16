using StackExchange.Redis;
using StoveDotnet.Redis;
using Xunit;

namespace StoveDotnet.Redis.AcceptanceTests;

public sealed class RedisTests
{
    [Fact]
    public async Task Named_instances_are_isolated_and_migrations_run()
    {
        await using var stove = await StoveBuilder.Create()
            .WithRedis("cache", o => o.Migrations.Add((ctx, _) => ctx.Multiplexer.GetDatabase().StringSetAsync("seeded", "yes")))
            .WithRedis("sessions")
            .StartAsync(TestContext.Current.CancellationToken);

        await stove.Test(async t =>
        {
            Assert.Equal("yes", (string?)await t.Redis("cache").Database().StringGetAsync("seeded"));
            Assert.False(await t.Redis("sessions").Database().KeyExistsAsync("seeded"));
            Assert.Throws<InvalidOperationException>(() => t.Redis());

            await t.Redis("sessions").Database().StringSetAsync("user:1", "token");
            Assert.Equal("token", (string?)await t.Redis("sessions").Database().StringGetAsync("user:1"));
        });
    }

    [Fact]
    public async Task Ordered_migrations_finish_before_the_application_receives_the_connection()
    {
        string? seenByApplication = null;
        var app = new ConfigurationProbe(async (configuration, ct) =>
        {
            // The application connects with its own client, using only what Stove exposed.
            await using var own = await ConnectionMultiplexer.ConnectAsync(configuration["Redis:ConnectionString"]!);
            seenByApplication = await own.GetDatabase().StringGetAsync("steps");
        });

        await using var stove = await StoveBuilder.Create()
            .WithRedis(o =>
            {
                o.ConfigureExposedConfiguration = c => [new("Redis:ConnectionString", c.ConnectionString)];
                o.Migrations.Add((ctx, _) => ctx.Multiplexer.GetDatabase().StringAppendAsync("steps", "second"), order: 2);
                o.Migrations.Add((ctx, _) => ctx.Multiplexer.GetDatabase().StringAppendAsync("steps", "first,"), order: 1);
            })
            .WithApplication(app)
            .StartAsync(TestContext.Current.CancellationToken);

        Assert.Equal("first,second", seenByApplication);
    }

    [Fact]
    public async Task Existing_server_skips_migrations_on_request_runs_cleanup_and_stays_usable()
    {
        await using var owner = await StoveBuilder.Create().WithRedis().StartAsync(TestContext.Current.CancellationToken);
        var connectionString = "";
        await owner.Test(t => { connectionString = t.Redis().ExposedConfiguration.ConnectionString; return Task.CompletedTask; });
        var cleanups = 0;

        var guest = await StoveBuilder.Create()
            .WithRedis(o =>
            {
                o.UseExisting(connectionString, runMigrations: false);
                o.Migrations.Add((_, _) => throw new InvalidOperationException("must not run"));
                o.Cleanup = (_, _) => { cleanups++; return Task.CompletedTask; };
            })
            .StartAsync(TestContext.Current.CancellationToken);
        await guest.Test(t => t.Redis().Database().StringSetAsync("written-by-guest", "yes"));
        await guest.DisposeAsync();

        Assert.Equal(1, cleanups);
        // Stove does not stop a server it did not start.
        await owner.Test(async t => Assert.Equal("yes", (string?)await t.Redis().Database().StringGetAsync("written-by-guest")));
    }

    [Fact]
    public async Task Unreachable_existing_server_fails_startup_before_the_application_starts()
    {
        var appStarted = false;
        var app = new ConfigurationProbe((_, _) => { appStarted = true; return Task.CompletedTask; });

        var error = await Assert.ThrowsAsync<StoveStartupException>(() => StoveBuilder.Create()
            .WithRedis("cache", o =>
            {
                o.UseExisting("127.0.0.1:1", runMigrations: false);
                o.ConfigureClient = c => { c.ConnectTimeout = 300; c.ConnectRetry = 1; };
            })
            .WithApplication(app)
            .StartAsync(TestContext.Current.CancellationToken));

        Assert.IsType<RedisConnectionException>(error.InnerException);
        Assert.Contains("'cache' failed to start", error.Message, StringComparison.Ordinal);
        Assert.False(appStarted);
    }

    [Fact]
    public async Task Failed_migration_and_cleanup_keep_both_errors_and_remove_the_container()
    {
        var migrationError = new InvalidOperationException("migration failed");
        var connectionString = "";

        var error = await Assert.ThrowsAsync<AggregateException>(() => StoveBuilder.Create()
            .WithRedis(o =>
            {
                o.Migrations.Add((ctx, _) => { connectionString = ctx.Configuration.ConnectionString; throw migrationError; });
                o.Cleanup = (_, _) => throw new InvalidOperationException("cleanup failed");
            })
            .StartAsync(TestContext.Current.CancellationToken));

        Assert.Same(migrationError, Assert.IsType<StoveStartupException>(error.InnerExceptions[0]).InnerException);
        Assert.Contains(error.Flatten().InnerExceptions, e => e.Message == "cleanup failed");
        var options = ConfigurationOptions.Parse(connectionString);
        options.ConnectTimeout = 300;
        options.ConnectRetry = 1;
        await Assert.ThrowsAsync<RedisConnectionException>(() => ConnectionMultiplexer.ConnectAsync(options));
    }

    [Fact]
    public async Task Client_customization_is_observed_by_the_server()
    {
        await using var stove = await StoveBuilder.Create()
            .WithRedis(o => o.ConfigureClient = c => c.ClientName = "stove-acceptance")
            .StartAsync(TestContext.Current.CancellationToken);

        await stove.Test(async t =>
            Assert.Equal("stove-acceptance", (string?)await t.Redis().Database().ExecuteAsync("CLIENT", "GETNAME")));
    }

    [Fact]
    public async Task Disposal_runs_cleanup_once_and_releases_the_client()
    {
        var cleanups = 0;
        var stove = await StoveBuilder.Create()
            .WithRedis(o => o.Cleanup = (_, _) => { cleanups++; return Task.CompletedTask; })
            .StartAsync(TestContext.Current.CancellationToken);
        RedisSystem? system = null;
        await stove.Test(t => { system = t.Redis(); return Task.CompletedTask; });

        await stove.DisposeAsync();
        await system!.DisposeAsync();

        Assert.Equal(1, cleanups);
        Assert.False(system.Multiplexer.IsConnected);
        await Assert.ThrowsAnyAsync<Exception>(() => system.Database().PingAsync());
    }

    [Fact]
    public async Task Cancelled_startup_is_reported_as_cancellation()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => StoveBuilder.Create().WithRedis().StartAsync(cancelled.Token));
    }

    private sealed class ConfigurationProbe(Func<IReadOnlyDictionary<string, string?>, CancellationToken, Task> start)
        : IApplicationUnderTest, IApplicationContext, IServiceProvider
    {
        public IServiceProvider Services => this;
        public Uri BaseAddress { get; } = new("http://localhost");
        public object? GetService(Type serviceType) => null;
        public async Task<IApplicationContext> StartAsync(IReadOnlyDictionary<string, string?> configuration, CancellationToken cancellationToken)
        {
            await start(configuration, cancellationToken);
            return this;
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
