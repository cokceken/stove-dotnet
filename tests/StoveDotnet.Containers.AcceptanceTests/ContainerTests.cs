using System.Net;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Xunit;

namespace StoveDotnet.Containers.AcceptanceTests;

public sealed class ContainerTests
{
    [Fact]
    public async Task Named_containers_have_independent_ports_files_and_owned_lifetimes()
    {
        Uri? address = null;
        await using var stove = await StoveBuilder.Create()
            .WithContainer("one", o => o.CreateContainer = () => Web("one").Build())
            .WithContainer("two", o => o.CreateContainer = () => Web("two").Build())
            .StartAsync(TestContext.Current.CancellationToken);
        await stove.Test(async t =>
        {
            Assert.Throws<InvalidOperationException>(() => t.Container());
            address = Address(t.Container("one").Container);
            var second = Address(t.Container("two").Container);
            Assert.NotEqual(address, second);
            using var client = new HttpClient();
            Assert.Equal("one", await client.GetStringAsync(address, t.CancellationToken));
            Assert.Equal("two", await client.GetStringAsync(second, t.CancellationToken));
            var result = await t.Container("one").Container.ExecAsync(["cat", "/www/index.html"], t.CancellationToken);
            Assert.Equal("one", result.Stdout);
        }, TestContext.Current.CancellationToken);
        await stove.DisposeAsync();
        await stove.DisposeAsync();
        using var after = new HttpClient();
        await Assert.ThrowsAsync<HttpRequestException>(() => after.GetStringAsync(address, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Initialization_failure_removes_the_started_container()
    {
        Uri? address = null;
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => StoveBuilder.Create()
            .WithContainer(o =>
            {
                o.CreateContainer = () => Web("ready").Build();
                o.InitializeAsync = (container, _) => { address = Address(container); throw new InvalidOperationException("initialization failed"); };
            }).StartAsync(TestContext.Current.CancellationToken));
        Assert.Equal("initialization failed", error.Message);
        using var client = new HttpClient();
        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetStringAsync(address, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Cancellation_reaches_initialization_and_rolls_back()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var entered = new TaskCompletionSource<Uri>(TaskCreationOptions.RunContinuationsAsynchronously);
        var starting = StoveBuilder.Create().WithContainer(o =>
        {
            o.CreateContainer = () => Web("ready").Build();
            o.InitializeAsync = async (container, ct) =>
            {
                entered.SetResult(Address(container));
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            };
        }).StartAsync(cancellation.Token);
        var address = await entered.Task.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => starting);
        using var client = new HttpClient();
        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetStringAsync(address, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Readiness_failure_rolls_back_before_application_start()
    {
        IContainer? native = null;
        var app = new UnexpectedApplication();
        await Assert.ThrowsAnyAsync<TimeoutException>(() => StoveBuilder.Create().WithContainer(o =>
        {
            o.CreateContainer = () => native = Web("ready")
                .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r.ForPort(8080)
                    .ForPath("/missing").ForStatusCode(HttpStatusCode.OK), w => w.WithTimeout(TimeSpan.FromSeconds(2))))
                .Build();
        }).WithApplication(app).StartAsync(TestContext.Current.CancellationToken));
        Assert.False(app.Started);
        Assert.NotNull(native);
        Assert.Equal(TestcontainersStates.Undefined, native.State);
    }

    [Fact]
    public async Task Already_running_container_is_rejected_without_taking_ownership()
    {
        await using var external = Web("external").Build();
        await external.StartAsync(TestContext.Current.CancellationToken);
        var address = Address(external);
        await Assert.ThrowsAsync<InvalidOperationException>(() => StoveBuilder.Create()
            .WithContainer(o => o.CreateContainer = () => external).StartAsync(TestContext.Current.CancellationToken));
        using var client = new HttpClient();
        Assert.Equal("external", await client.GetStringAsync(address, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Log_excerpts_are_opt_in_bounded_redacted_and_fail_closed()
    {
        ContainerOptions? options = null;
        await using var stove = await StoveBuilder.Create().WithContainer("custom", o =>
        {
            options = o;
            o.CreateContainer = () => Web("ready", log: true).WithEnvironment("PRIVATE_VALUE", "container-secret").Build();
        }).StartAsync(TestContext.Current.CancellationToken);
        async Task<StoveTestFailedException> Fail() => await Assert.ThrowsAsync<StoveTestFailedException>(() => stove.Test(_ =>
            throw new StoveAssertionException("scenario failed"), TestContext.Current.CancellationToken));
        var quiet = await Fail();
        Assert.Contains("container:custom", quiet.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("container-secret", quiet.ToString(), StringComparison.Ordinal);
        options!.IncludeLogs = true;
        options.MaxLogLength = 1024;
        options.RedactLogs = value => value.Replace("container-secret", "[private]", StringComparison.Ordinal);
        var safe = await Fail();
        Assert.Contains("[private]", safe.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("container-secret", safe.ToString(), StringComparison.Ordinal);
        options.MaxLogLength = 80;
        Assert.Contains("[truncated]", (await Fail()).Message, StringComparison.Ordinal);
        options.RedactLogs = _ => throw new InvalidOperationException("container-secret");
        var closed = await Fail();
        Assert.Contains("[redaction failed]", closed.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("container-secret", closed.ToString(), StringComparison.Ordinal);
    }

    private static ContainerBuilder Web(string body, bool log = false) => new ContainerBuilder("busybox:1.37")
        .WithResourceMapping(System.Text.Encoding.UTF8.GetBytes(body), "/www/index.html")
        .WithCommand(log
            ? ["sh", "-c", "echo $PRIVATE_VALUE; printf '%0500d\\n' 0; exec busybox httpd -f -p 8080 -h /www"]
            : ["busybox", "httpd", "-f", "-p", "8080", "-h", "/www"])
        .WithPortBinding(8080, true)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r.ForPort(8080).ForPath("/")));

    private static Uri Address(IContainer container) => new UriBuilder("http", container.Hostname, container.GetMappedPublicPort(8080)).Uri;

    private sealed class UnexpectedApplication : IApplicationUnderTest
    {
        public bool Started { get; private set; }
        public Task<IApplicationContext> StartAsync(IReadOnlyDictionary<string, string?> configuration, CancellationToken cancellationToken)
        {
            Started = true;
            throw new InvalidOperationException("Application must not start before readiness.");
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
