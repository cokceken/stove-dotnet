using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Time.Testing;
using StoveDotnet.AspNetCore;
using StoveDotnet.Http;
using StoveDotnet.Time;
using Xunit;

namespace StoveDotnet.Time.AcceptanceTests;

// All tests that advance this fixture's clock belong to this collection. Other fixtures own different clocks.
[CollectionDefinition("shared application clock", DisableParallelization = true)]
public sealed class SharedClockCollection : ICollectionFixture<SharedClockFixture>;

public sealed class SharedClockFixture : IAsyncLifetime
{
    public FakeTimeProvider Clock { get; } = new();
    public Stove Stove { get; private set; } = null!;
    public async ValueTask InitializeAsync() => Stove = await StoveBuilder.Create()
        .WithAspNetCoreApplication<ClockAppMarker>(o => o.ConfigureWebHost = web => web.ConfigureTestServices(s => s.UseStoveTime(Clock)))
        .WithHttpClient().StartAsync(TestContext.Current.CancellationToken);
    public async ValueTask DisposeAsync() { GC.SuppressFinalize(this); await Stove.DisposeAsync(); }
}

[Collection("shared application clock")]
public sealed class SharedClockMinuteTests(SharedClockFixture fixture)
{
    [Fact]
    public Task Advance_from_current_time_without_assuming_test_order() => fixture.Stove.Test(async t =>
    {
        var before = t.Clock().GetUtcNow();
        t.Clock().Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(before.AddMinutes(1), (await t.Http().Get<ClockReply>("/clock")).Body.Now);
    }, TestContext.Current.CancellationToken);
}

[Collection("shared application clock")]
public sealed class SharedClockHourTests(SharedClockFixture fixture)
{
    [Fact]
    public Task All_clock_mutations_share_the_serialized_collection() => fixture.Stove.Test(async t =>
    {
        var before = t.Clock().GetUtcNow();
        t.Clock().Advance(TimeSpan.FromHours(1));
        Assert.Equal(before.AddHours(1), (await t.Http().Get<ClockReply>("/clock")).Body.Now);
    }, TestContext.Current.CancellationToken);
}

public sealed record ClockReply(DateTimeOffset Now);
