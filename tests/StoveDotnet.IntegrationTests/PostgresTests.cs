using Npgsql;
using StoveDotnet.Postgres;
using Xunit;

namespace StoveDotnet.IntegrationTests;

public sealed class PostgresTests
{
    [Fact]
    public async Task Starts_container_runs_migrations_and_queries()
    {
        var cleanedUp = false;
        var stove = await StoveBuilder.Create()
            .WithPostgres(o =>
            {
                o.ConfigureExposedConfiguration = c => [new("ConnectionStrings:Orders", c.ConnectionString)];
                o.Migrations.Add((ctx, ct) => Execute(ctx.DataSource, "create table products (id int primary key, name text not null)", ct));
                o.Migrations.Add((ctx, ct) => Execute(ctx.DataSource, "insert into products values (0, 'seeded by migration')", ct), order: 1);
                o.Cleanup = (_, _) =>
                {
                    cleanedUp = true;
                    return Task.CompletedTask;
                };
            })
            .StartAsync(TestContext.Current.CancellationToken);

        await using (stove)
        {
            await stove.Test(async t =>
            {
                var db = t.Postgres();
                Assert.Equal(1, await db.Execute("insert into products values (@id, @name)", new("id", 1), new("name", "chair")));

                await db.ShouldQuery(
                    "select id, name from products order by id",
                    r => (Id: r.GetInt32(0), Name: r.GetString(1)),
                    rows => Assert.Equal([(0, "seeded by migration"), (1, "chair")], rows));

                Assert.StartsWith("Host=", db.ExposedConfiguration.ConnectionString, StringComparison.Ordinal);
            });
        }

        Assert.True(cleanedUp);
    }

    [Fact]
    public async Task Uses_an_existing_instance_when_provided()
    {
        await using var external = await StoveBuilder.Create().WithPostgres(o => o.Database = "external").StartAsync(TestContext.Current.CancellationToken);
        var connectionString = await ConnectionString(external);

        await using var stove = await StoveBuilder.Create()
            .WithPostgres("provided", o => o.UseExisting(connectionString, runMigrations: false))
            .StartAsync(TestContext.Current.CancellationToken);

        await stove.Test(async t =>
        {
            var rows = await t.Postgres().Query("select current_database()", r => r.GetString(0));
            Assert.Equal(["external"], rows);
        });
    }

    private static async Task<string> ConnectionString(Stove stove)
    {
        var connectionString = string.Empty;
        await stove.Test(t =>
        {
            connectionString = t.Postgres().ExposedConfiguration.ConnectionString;
            return Task.CompletedTask;
        });
        return connectionString;
    }

    private static async Task Execute(NpgsqlDataSource dataSource, string sql, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(sql);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
