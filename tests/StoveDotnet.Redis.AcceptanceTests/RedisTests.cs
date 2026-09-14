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
}
