# Databases

PostgreSQL, SQL Server, MongoDB, MySQL and Redis each have their own package. The application uses its normal driver
and reads configuration; it does not reference Stove. Every module can start a container or connect to an existing
server (see [existing instances](setup.md#existing-instances)), runs ordered migrations or setup before the application
starts, and exposes its native client.

`Query` reads once and returns the rows for your assertion library. Wrap the query and its assertion in
`Eventually.AssertAsync` when the application writes asynchronously.

## PostgreSQL

Reference `StoveDotnet.Postgres`. `Execute(sql, params)` and `Query<T>(sql, map, params)` take `NpgsqlParameter`s and
an `NpgsqlDataReader` mapper; `DataSource` gives raw Npgsql access. Options: `Migrations`, `Cleanup`, `Image`,
`ConfigureContainer`, `ConfigureDataSource` (customizes the `NpgsqlDataSourceBuilder` used by Stove) and `UseExisting`.

## SQL Server

Reference `StoveDotnet.SqlServer` and use `StoveDotnet.SqlServer` and `Microsoft.Data.SqlClient` namespaces:

```csharp
var stove = await StoveBuilder.Create()
    .WithSqlServer("orders", o =>
    {
        o.Database = "orders";
        o.ConfigureExposedConfiguration = c => [new("ConnectionStrings:Orders", c.ConnectionString)];
        o.Migrations.Add(async (ctx, ct) =>
        {
            await using var connection = await ctx.OpenConnectionAsync(ct);
            await using var command = connection.CreateCommand();
            command.CommandText = "create table orders (id int primary key, status nvarchar(50))";
            await command.ExecuteNonQueryAsync(ct);
        });
    })
    .WithAspNetCoreApplication<Program>()
    .StartAsync();

await stove.Test(async t =>
{
    await t.SqlServer("orders").Execute("insert into orders values (@id, @status)",
        new SqlParameter("id", 1), new SqlParameter("status", "Created"));
    Assert.Equal(["Created"], await t.SqlServer("orders").Query("select status from orders where id = @id",
        r => r.GetString(0), new SqlParameter("id", 1)));
});
await stove.DisposeAsync();
```

Managed instances create the requested database before migrations. `UseExisting(connectionString, runMigrations: true)`
uses the database in that connection string and does not create it. `Image`, `Password` and `ConfigureContainer` apply
to managed instances. The default image follows the SQL Server 2022 tag; set `Image` to a fixed tag or digest for a
reproducible environment. Running the SQL Server Testcontainers image accepts its EULA; use an existing instance when
your environment cannot run that image.

`OpenConnectionAsync(ct)` returns a native `SqlConnection` for transactions, bulk copy and other provider operations.
The caller disposes it. `ConfigureConnection` customizes each client connection before opening (for example, an
access-token callback); migrations and cleanup receive the same connection factory. It does not configure the
application's own SQL client.

## Redis

Reference `StoveDotnet.Redis`. Tests use StackExchange.Redis directly through `Multiplexer` and `Database(db)`.
Options: `Migrations`, `Cleanup`, `Image`, `ConfigureContainer` and `UseExisting`.

## Test isolation

A database belongs to a running Stove environment, not an individual `stove.Test`. Migrations run at environment startup
and cleanup at shutdown. Named managed instances have separate containers; two named existing connections may still
point at the same database. Correlation does not isolate rows or roll back application writes.

Use unique identifiers per test for parallel tests. For tests that require an empty database, use a dedicated environment
or explicitly reset data between sequential tests. A transaction opened by the test does not include writes made by the
application through its own connections.

## MongoDB

Reference `StoveDotnet.MongoDb` and use the `StoveDotnet.MongoDb`, `MongoDB.Driver` and `MongoDB.Bson` namespaces.
Register with `WithMongoDb(configure)` or `WithMongoDb(name, configure)`; resolve with `t.MongoDb(name)`.

| Option/API | Behavior |
| --- | --- |
| `Image` | Defaults to `mongo:8.0`; override explicitly for another image |
| `Database` | Defaults to `stove`; the database is created by its first write/setup operation |
| `ReplicaSet` | Defaults to `stove-rs`, a single-node replica set; set null for standalone |
| `ConfigureContainer` | Customize the native `MongoDbBuilder` before building |
| `ConfigureClient` | Customize `MongoClientSettings` before Stove creates its client |
| `UseExisting(connectionString, database, runSetup: true)` | Explicit database selection on an external server; no container or topology changes |
| `Setup.Add((ctx, ct) => ..., order)` | Ordered collection/index/seed setup before application startup |
| `Cleanup` | Receives the setup context at environment disposal, including on external endpoints |
| `ExposedConfiguration` | Original connection string and selected database, mapped to application keys by the caller |
| `Client`, `Database`, `Collection<T>(name, settings)` | Native APIs; collection settings and native BSON serializers remain available |
| `Insert<T>(collection, document)` | Inserts one document with the current test's cancellation token |
| `Query<T>(collection, filter)` | Reads all current matches once using a native `FilterDefinition<T>` |

`MongoDbSetupContext` exposes `Client`, `Database` and `Configuration`. Setup uses the core ordered callback collection;
there is no persisted migration history or schema management. Setup reruns each start unless explicitly disabled for
an existing endpoint. Write idempotent setup when reusing a database. MongoDB's `ping` verifies connectivity before
setup/application startup even when setup is disabled; it does not prove that every collection operation is authorized.

Configure both the connection string and database in your application:

```csharp
.WithMongoDb("documents", o =>
{
    o.ConfigureExposedConfiguration = c =>
        [new("Mongo:ConnectionString", c.ConnectionString), new("Mongo:Database", c.Database)];
    o.ConfigureClient = settings => settings.ApplicationName = "stove-tests";
    o.Setup.Add(async (ctx, ct) =>
    {
        await ctx.Database.CreateCollectionAsync("records", cancellationToken: ct);
        await ctx.Database.GetCollection<BsonDocument>("records").Indexes.CreateOneAsync(
            new CreateIndexModel<BsonDocument>(Builders<BsonDocument>.IndexKeys.Ascending("value")),
            cancellationToken: ct);
    });
});
```

Stove's client hook does not configure the application's client or install global BSON conventions. Use native
attributes/serializer configuration for your document models. Queries do not imply sort order, retries or paging;
use the native collection API for projections, sorting, pagination and sessions. Dispose sessions/cursors you create
and pass `t.CancellationToken` to native operations. Stove owns its client in both runtime modes.

The default topology is verified with a real transaction commit and abort using a native session. A transaction on
Stove's session does not include the application's independent session, and Stove does not roll back application writes
automatically. Standalone mode supports document operations but not transactions. Existing endpoints retain their
own topology and transaction constraints. Sharding, failover and hosted MongoDB-compatible service parity are untested.
The default topology uses Testcontainers' [replica-set support](https://dotnet.testcontainers.org/modules/mongodb/).

## MySQL

Reference `StoveDotnet.MySql` and use `StoveDotnet.MySql` and `MySqlConnector` namespaces.
Register with `WithMySql(configure)` or `WithMySql(name, configure)`; resolve with `t.MySql(name)`.

| Option/API | Behavior |
| --- | --- |
| `Image` | Defaults to `mysql:8.4` |
| `Database`, `Username`, `Password` | Each defaults to `stove`; managed database/user provisioning is delegated to Testcontainers |
| `ConfigureContainer` | Customize the native `MySqlBuilder` |
| `ConfigureDataSource` | Customize Stove's `MySqlDataSourceBuilder`, including authentication and connection-open callbacks |
| `UseExisting(connectionString, runMigrations: true)` | Uses an external endpoint/database without provisioning it |
| `Migrations.Add((ctx, ct) => ..., order)` | Ordered setup before application startup; context exposes `DataSource` and `Configuration` |
| `Cleanup` | Receives the data source before disposal, also for external endpoints |
| `ExposedConfiguration` | Connection string, host, port, database, username and password |
| `DataSource` | Native `MySqlDataSource`; caller disposes connections obtained from it |
| `Execute(sql, params MySqlParameter[])` | Runs a statement and returns affected rows |
| `Query<T>(sql, map, params MySqlParameter[])` | Reads current rows once with a `MySqlDataReader` mapper |

The module opens a real connection before exposing configuration, including when migrations are disabled. Every DSL
operation owns and disposes its connection/command/reader and uses the test's cancellation token. Client customization
affects Stove's data source; the application still configures its own driver. Native connection callbacks are covered
by reading their session variable back from the server. See the driver's
[data-source builder API](https://mysqlconnector.net/api/mysqlconnector/mysqldatasourcebuildertype/).

The initial supported/tested combination is MySqlConnector 2.6.2 with MySQL 8.4. The tests verify Unicode values
including supplementary characters with the default image. MariaDB and other MySQL versions require their own
compatibility evidence; an image override alone does not establish compatibility.

## MongoDB and MySQL: ownership and verification

Both modules dispose owned clients even if cleanup fails, then attempt owned-container removal. Cleanup failures and
startup/rollback failures are retained. External servers are never stopped, but an explicitly configured cleanup
callback can mutate their data. There is no automatic per-test reset: use unique identifiers or explicitly isolated
databases when scopes overlap. Named registration alone does not isolate test data. Queries are one-time reads;
opt into `Eventually` where application effects are asynchronous.

The independent MongoDB acceptance suite includes a native-driver app, BSON ObjectIds/field mapping, indexes and
ordered setup, both directions of HTTP/database interaction, named routing, overlapping scopes, existing endpoints,
readiness, cancellation, disposal failures, rollback and native transactions. The MySQL suite reuses the relational
contract with its own driver/app and adds Unicode and cancellation-within-scope cases. Neither adds dependencies to
OrderService or the other provider suites.

Both defaults were exercised locally on Windows x64 with Podman and .NET 10. The image tags select version lines and
are not immutable image digests. This is not a full architecture/runtime matrix. See
[compatibility](compatibility.md) for the tested versions and the shared CI workflow for remote checks.
