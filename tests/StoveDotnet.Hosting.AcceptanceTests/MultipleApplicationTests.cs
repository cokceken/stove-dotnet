using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using StoveDotnet.AspNetCore;
using StoveDotnet.Hosting;
using StoveDotnet.Http;
using Xunit;

namespace StoveDotnet.Hosting.AcceptanceTests;

public sealed class MultipleApplicationTests
{
    [Fact]
    public async Task Api_failure_logs_belong_to_the_host_that_handled_the_request()
    {
        await using var stove = await StoveBuilder.Create()
            .WithAspNetCoreApplication<Program>("first", o => o.Configuration["TestApp:Greeting"] = "first")
            .WithAspNetCoreApplication<Program>("second", o => o.Configuration["TestApp:Greeting"] = "second")
            .WithHttpClient("first", o => o.ApplicationName = "first")
            .StartAsync(TestContext.Current.CancellationToken);
        var error = await Assert.ThrowsAsync<StoveTestFailedException>(() => stove.Test(async t =>
        {
            await t.Http("first").Get("/boom");
            throw new InvalidOperationException("unexpected server response");
        }, TestContext.Current.CancellationToken));
        Assert.Contains("application:first", error.Message, StringComparison.Ordinal);
        Assert.Contains("About to fail", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("application:second", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Partially_started_worker_is_disposed_and_error_names_the_application()
    {
        var worker = new ProbeWorker { FailStart = true };
        var builder = StoveBuilder.Create().WithHostApplication("worker",
            _ => Host.CreateDefaultBuilder().ConfigureServices(s => s.AddSingleton<IHostedService>(_ => worker)).Build());
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => builder.StartAsync(TestContext.Current.CancellationToken));
        Assert.Contains("worker", error.Message, StringComparison.Ordinal);
        Assert.Contains("start failed", error.InnerException!.Message, StringComparison.Ordinal);
        Assert.True(worker.Disposed);
    }

    [Fact]
    public async Task Failure_output_labels_worker_logs_and_excludes_other_test_traces()
    {
        await using var stove = await StoveBuilder.Create()
            .WithHostApplication("worker", _ => Host.CreateDefaultBuilder().Build())
            .StartAsync(TestContext.Current.CancellationToken);
        await stove.Test(t =>
        {
            t.GetApplication("worker").Services.GetRequiredService<ILoggerFactory>().CreateLogger("processing").LogInformation("previous-test-message");
            return Task.CompletedTask;
        }, TestContext.Current.CancellationToken);
        var error = await Assert.ThrowsAsync<StoveTestFailedException>(() => stove.Test(t =>
        {
            t.GetApplication("worker").Services.GetRequiredService<ILoggerFactory>().CreateLogger("processing").LogInformation("current-test-message");
            throw new InvalidOperationException("contract failed");
        }, TestContext.Current.CancellationToken));
        Assert.Contains("application:worker", error.Message, StringComparison.Ordinal);
        Assert.Contains("current-test-message", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("previous-test-message", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Worker_is_disposed_even_when_stop_fails()
    {
        var worker = new ProbeWorker { FailStop = true };
        var stove = await StoveBuilder.Create()
            .WithHostApplication("worker", _ => Host.CreateDefaultBuilder().ConfigureServices(s => s.AddSingleton<IHostedService>(_ => worker)).Build())
            .StartAsync(TestContext.Current.CancellationToken);
        var error = await Assert.ThrowsAsync<AggregateException>(() => stove.DisposeAsync().AsTask());
        Assert.Contains("worker", error.ToString(), StringComparison.Ordinal);
        Assert.True(worker.Disposed);
    }
    [Fact]
    public async Task Two_real_apis_bind_distinct_clients_and_service_scopes()
    {
        await using var stove = await StoveBuilder.Create()
            .WithAspNetCoreApplication<Program>("first", o => o.Configuration["TestApp:Greeting"] = "first")
            .WithAspNetCoreApplication<Program>("second", o => o.Configuration["TestApp:Greeting"] = "second")
            .WithHttpClient("first", o => o.ApplicationName = "first")
            .WithHttpClient("second", o => o.ApplicationName = "second")
            .StartAsync(TestContext.Current.CancellationToken);
        Assert.NotEqual(stove.GetApplication("first").BaseAddress, stove.GetApplication("second").BaseAddress);
        await stove.Test(async t =>
        {
            var replies = await Task.WhenAll(t.Http("first").Get<Greeting>("/greeting"), t.Http("second").Get<Greeting>("/greeting"));
            Assert.Equal(["first", "second"], replies.Select(r => r.Body.Message));
            await t.Using<GreetingService>("second", service => { Assert.Equal("second", service.Message); return Task.CompletedTask; });
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Worker_uses_real_host_configuration_and_is_stopped_and_disposed()
    {
        var worker = new ProbeWorker();
        var stove = await StoveBuilder.Create()
            .WithHostApplication("worker", configuration =>
            {
                var builder = Host.CreateApplicationBuilder();
                builder.Configuration.AddInMemoryCollection(configuration);
                Assert.Equal("worker-only", builder.Configuration["Role"]);
                builder.Services.AddSingleton<IHostedService>(_ => worker);
                return builder.Build();
            }, o => o.Configuration["Role"] = "worker-only")
            .WithAspNetCoreApplication<Program>("api", o => o.Configuration["TestApp:Greeting"] = "api")
            .WithHttpClient("api", o => o.ApplicationName = "api")
            .StartAsync(TestContext.Current.CancellationToken);
        Assert.True(worker.Started);
        Assert.Throws<InvalidOperationException>(() => stove.GetApplication("worker").BaseAddress);
        await stove.Test(async t => Assert.Equal("api", (await t.Http("api").Get<Greeting>("/greeting")).Body.Message), TestContext.Current.CancellationToken);
        await stove.DisposeAsync();
        Assert.True(worker.Stopped);
        Assert.True(worker.Disposed);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("missing")]
    [InlineData("worker")]
    public async Task Invalid_http_target_fails_startup_and_disposes_worker(string? target)
    {
        var worker = new ProbeWorker();
        var builder = StoveBuilder.Create()
            .WithHostApplication("worker", _ => Host.CreateDefaultBuilder().ConfigureServices(s => s.AddSingleton<IHostedService>(_ => worker)).Build())
            .WithAspNetCoreApplication<Program>("api", o => o.Configuration["TestApp:Greeting"] = "api")
            .WithHttpClient(o => o.ApplicationName = target);
        await Assert.ThrowsAsync<InvalidOperationException>(() => builder.StartAsync(TestContext.Current.CancellationToken));
        Assert.True(worker.Stopped);
        Assert.True(worker.Disposed);
    }

    private sealed class ProbeWorker : IHostedService, IDisposable
    {
        public bool FailStop { get; init; }
        public bool FailStart { get; init; }
        public bool Started { get; private set; }
        public bool Stopped { get; private set; }
        public bool Disposed { get; private set; }
        public Task StartAsync(CancellationToken cancellationToken)
        {
            Started = true;
            if (FailStart) throw new InvalidOperationException("start failed");
            return Task.CompletedTask;
        }
        public Task StopAsync(CancellationToken cancellationToken)
        {
            Stopped = true;
            if (FailStop) throw new InvalidOperationException("stop failed");
            return Task.CompletedTask;
        }
        public void Dispose() { Disposed = true; }
    }
}
