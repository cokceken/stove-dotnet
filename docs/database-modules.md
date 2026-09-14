# MongoDB and MySQL

These modules are implemented in this repository. Publication and remote CI status are separate from local verification.
The application uses its normal database driver and reads configuration; it does not reference Stove.

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
| `ShouldQuery<T>(collection, filter, assert)` | Passes the current matches to the caller's assertion callback |

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
| `ShouldQuery<T>(sql, map, assert, params MySqlParameter[])` | Passes mapped rows to the caller's assertion callback |

The module opens a real connection before exposing configuration, including when migrations are disabled. Every DSL
operation owns and disposes its connection/command/reader and uses the test's cancellation token. Client customization
affects Stove's data source; the application still configures its own driver. Native connection callbacks are covered
by reading their session variable back from the server. See the driver's
[data-source builder API](https://mysqlconnector.net/api/mysqlconnector/mysqldatasourcebuildertype/).

The initial supported/tested combination is MySqlConnector 2.6.2 with MySQL 8.4. The tests verify Unicode values
including supplementary characters with the default image. MariaDB and other MySQL versions require their own
compatibility evidence; an image override alone does not establish compatibility.

## Ownership, isolation and verification

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
are not immutable image digests. This is not a full architecture/runtime matrix. See [roadmap verification](../ROADMAP.md)
for the recorded results and the shared CI workflow for remote checks.
