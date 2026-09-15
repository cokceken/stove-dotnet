# Module conventions

For priorities and implementation status, see the [roadmap](../ROADMAP.md). For the agreed design rationale, see
[architecture decisions](decisions.md).

Each module is a separate `StoveDotnet.Xxx` package with a reference to the core and its native client/container packages.
Keep provider-specific clients and parameters accessible; do not hide them behind a universal database API.

## Registration and lifecycle

- Offer `WithXxx(configure)` and `WithXxx(name, configure)` plus `t.Xxx(name)`.
- Implement only the lifecycle capabilities needed by the module. `IRunAware` runs before the application, in parallel
  with other systems. It must finish readiness checks and migrations before returning.
- Expose typed connection details and let the application choose its configuration keys. Two modules cannot own the
  same key. Native-client customization affects Stove's client, not the application's client.
- Distinguish managed infrastructure from an existing endpoint. Dispose clients owned by Stove in both modes, and
  remove only containers owned by Stove. Explain whether cleanup callbacks run against existing services.
- Make disposal safe after partial startup and on repeated calls. Use `ExposingSystem.DisposeResourcesAsync` to attempt
  cleanup, client disposal and container disposal independently. Release temporary connections if opening fails.
- Keep migration ordering local to each module; registration order does not establish dependencies between systems.
- Application adapters are separate from dependency systems: applications start sequentially after dependency readiness.
  Use `IAfterApplicationsStarted` and `stove.GetApplication(name)` for explicit application binding. Legacy
  `IAfterApplicationStarted` hooks resolve the default/only application and therefore reject ambiguous topologies.
  An application adapter may implement `IFailureDetailsProvider`; Stove prefixes its evidence with the application name.

## Test operations

DSL operations require `StoveTestContext.Require()`, use its cancellation token, and create a correlated activity when
appropriate. Native clients are escape hatches; document who disposes returned resources and passes cancellation.
Assertion callbacks use the caller's assertion library. Database queries run once; callers can opt into `Eventually`.

Document isolation explicitly. A named instance is not a per-test reset. Never clear shared database state at the end of
a parallel test. Messaging observation and successful application processing are different guarantees.

## Acceptance tests

`tests/StoveDotnet.Testing/RelationalModuleTests.cs` defines the provider-neutral mechanics contract, and
`RelationalApplicationTests.cs` defines the real-application contract. PostgreSQL, SQL Server and MySQL have separate
acceptance projects, adapters and fixtures; neither references the other provider. Add a provider adapter and a small
native-client application for another relational module, or reuse these requirements for another category:

| Behavior | Evidence |
| --- | --- |
| Managed service | Start a real container, use the native client and the DSL |
| Readiness | Reject an unavailable existing database before starting the application, even without migrations |
| Configuration and migrations | Confirm ordered migrations finish before the app receives connection details |
| Existing endpoint | Skip migrations when requested; leave the external server usable after disposal |
| Named resolution | Resolve each instance explicitly and reject an ambiguous unnamed lookup |
| Client customization | Observe a configured client property on the real server |
| Parameters | Round-trip data containing SQL syntax as data |
| Cancellation | Pass cancellation through to native operations |
| Failure cleanup | Fail a migration and cleanup, preserve both errors, and verify the managed endpoint is gone |
| Resource ownership | Verify disposed clients are unusable and repeated disposal does not repeat cleanup |
| Application writes | Send HTTP requests, then query the real database through Stove |
| Application reads | Seed through Stove, then read through HTTP |
| Real startup | The application queries migration-created rows before listening for requests |
| Named routing | Write the same identifier to two databases and verify different values |
| Concurrent scopes | Explicitly overlap scopes with a barrier and verify unique records |
| Application failures | Preserve failed assertions and available test identity/diagnostics |

Core lifecycle tests additionally cover rollback while another system is starting, startup cancellation and reverse
shutdown order with an application that fails to stop. New modules should also have a README example and participate
in `scripts/package-smoke-test.sh`, so the packaged public API is compiled from an isolated NuGet cache.

The real-application fixtures register modules through `WithPostgres`/`WithSqlServer`/`WithMySql`, while lifecycle tests may
construct systems directly to inspect partial startup and disposal. Test apps under `tests/TestApps` reference native
clients only. Keep business-like composition scenarios in OrderService instead of adding every provider to it.

CI and release share `.github/workflows/test-suites.yml`. Each suite builds only its project and dependencies in its
own job, with matrix fail-fast disabled. Adding a module suite requires adding it to that matrix; package creation and
release wait for all suites to pass.

MongoDB uses its own document-oriented contract and native-client application. See [MongoDB and MySQL](database-modules.md)
for their concrete APIs and tested boundaries.

## Broker modules

Kafka and RabbitMQ use a shared internal scoped buffer with explicit correlation policy and count/byte retention limits.
Test scope hooks release completed records while preserving bounded failure evidence. Overflow must invalidate both
positive and negative observation assertions, not silently discard evidence. Native headers must be copied before a
broker callback releases its delivery memory. See [messaging guarantees](messaging.md) for migration notes and semantics.

RabbitMQ owns an exclusive observation queue with explicit exchange bindings and never consumes application work
queues. Its independent native-client app proves application processing through an HTTP-readable effect. Confirms,
mandatory routing, observed copies and processing have separate tests. The suite also covers lost/cancelled observers,
existing endpoints, cleanup failures, overlapping scopes and packaged API consumption.
