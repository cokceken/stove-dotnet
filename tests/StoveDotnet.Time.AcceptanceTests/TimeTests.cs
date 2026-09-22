using System.Net;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;
using StoveDotnet.AspNetCore;
using StoveDotnet.Hosting;
using StoveDotnet.Http;
using StoveDotnet.Time;
using Xunit;

namespace StoveDotnet.Time.AcceptanceTests;

public sealed class TimeTests
{
    [Fact]
    public async Task Advancing_clock_completes_http_delay_and_hosted_worker_timers()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero));
        await using var stove = await Api("api", clock).WithHttpClient(o => o.ApplicationName = "api").StartAsync(TestContext.Current.CancellationToken);
        await stove.Test(async t =>
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/delay");
            var delayed = t.Http().Send<State>(request);
            await Eventually.UntilAsync<State>(async ct =>
            {
                using var read = new HttpRequestMessage(HttpMethod.Get, "/clock");
                return (await t.Http().Send<State>(read, ct)).Expect(HttpStatusCode.OK).Body;
            }, state => state.Waiting, TimeSpan.FromSeconds(5), cancellationToken: t.CancellationToken);
            Assert.False(delayed.IsCompleted);
            t.Clock("api").Advance(TimeSpan.FromHours(1));
            Assert.Equal(clock.GetUtcNow(), (await delayed.WaitAsync(TimeSpan.FromSeconds(5), t.CancellationToken)).Body.Now);
            var state = (await t.Http().Get<State>("/clock")).Expect(HttpStatusCode.OK).Body;
            Assert.True(state.Ticks > 0);
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Two_applications_can_explicitly_share_one_clock_and_stop_their_timers()
    {
        var clock = new FakeTimeProvider();
        await using var stove = await Api("api", clock)
            .WithHostApplication("worker", _ =>
            {
                var builder = Host.CreateApplicationBuilder();
                builder.Services.UseStoveTime(clock);
                builder.Services.AddSingleton<TimerWorker>();
                builder.Services.AddHostedService(s => s.GetRequiredService<TimerWorker>());
                return builder.Build();
            }).StartAsync(TestContext.Current.CancellationToken);
        var worker = stove.GetApplication("worker").Services.GetRequiredService<TimerWorker>();
        await stove.Test(t =>
        {
            Assert.Same(t.Clock("api"), t.Clock("worker"));
            t.Clock("api").Advance(TimeSpan.FromMinutes(1));
            Assert.True(worker.Ticks > 0);
            return Task.CompletedTask;
        }, TestContext.Current.CancellationToken);
        await stove.DisposeAsync();
        var ticks = worker.Ticks;
        clock.Advance(TimeSpan.FromMinutes(10));
        Assert.Equal(ticks, worker.Ticks);
    }

    [Fact]
    public async Task Independent_fixture_clocks_can_advance_in_parallel_without_cross_talk()
    {
        async Task<DateTimeOffset> Run(int hours)
        {
            var clock = new FakeTimeProvider(new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero));
            await using var stove = await Api("api", clock).WithHttpClient().StartAsync(TestContext.Current.CancellationToken);
            DateTimeOffset observed = default;
            await stove.Test(async t =>
            {
                t.Clock().Advance(TimeSpan.FromHours(hours));
                observed = (await t.Http().Get<State>("/clock")).Body.Now;
            }, TestContext.Current.CancellationToken);
            return observed;
        }
        var values = await Task.WhenAll(Run(1), Run(2));
        Assert.Equal(TimeSpan.FromHours(1), values[1] - values[0]);
    }

    private static StoveBuilder Api(string name, FakeTimeProvider clock) => StoveBuilder.Create()
        .WithAspNetCoreApplication<ClockAppMarker>(name, o => o.ConfigureWebHost = web => web.ConfigureTestServices(s => s.UseStoveTime(clock)));
    private sealed record State(DateTimeOffset Now, int Ticks, bool Waiting);
}
