# Project roadmap

Updated: 2026-09-14. Original baseline: `09e4a04` (`individual tests for modules`); MongoDB/MySQL now implemented.

StoveDotnet's next direction is broader module coverage, starting with databases. The lifecycle and test structure
are now suitable for adding modules incrementally. Each addition should include a useful testing experience, a real
application acceptance suite and a consumable package.

This document separates implemented work from accepted decisions and proposed future scope. **Implemented** means
present in the repository; it does not imply a particular NuGet release or a successful remote CI run. Future ordering
is a planning recommendation, not a delivery-date commitment. See [architecture decisions](docs/decisions.md) for the
reasoning and [module conventions](docs/modules.md) for implementation requirements.

## Implemented baseline

| Area | Present behavior |
| --- | --- |
| Core | Small lifecycle capabilities, named instances, typed configuration injection, migrations, cancellation, test correlation and failure wrapping |
| Application hosting | Real ASP.NET Core application on Kestrel, HTTP DSL and access to application services |
| Dependencies | PostgreSQL, SQL Server, MongoDB, MySQL, Redis, Kafka and named in-process WireMock servers |
| Telemetry | OTLP traces/logs, test correlation and failure details |
| Lifecycle hardening | Attempt all disposal steps despite cleanup failures; retain startup and rollback errors; wait for in-flight starts before rollback |
| Databases | Managed and existing endpoints, native clients, migration ordering, readiness before application startup for PostgreSQL/SQL Server/MySQL; MongoDB has ordered collection/index/seed setup and native transactions, native-client customization |
| Kafka absence assertions | `ShouldNotBePublished` observes for an explicit interval; it does not prove absence beyond that interval or through observer lag |
| Test organization | Core-only unit tests; separate hosting, PostgreSQL, SQL Server, MongoDB, MySQL, Redis and Kafka suites; provider-neutral database contracts |
| Real test applications | Hosting, PostgreSQL, SQL Server, MongoDB and MySQL apps under `tests/TestApps`, with no Stove dependency |
| Composition example | OrderService retains PostgreSQL, Redis, Kafka and external HTTP dependencies; eight active tests cover workflows, retrieval, rejection, concurrency and diagnostics |
| CI and packages | Shared CI/release matrix with separate suite jobs, fail-fast disabled, packaging gated on tests and an isolated package smoke test |

SQL Server is implemented; it is no longer a future-module candidate. The repository contains eleven library packages:
core, ASP.NET Core, HTTP, telemetry, PostgreSQL, SQL Server, MongoDB, MySQL, Redis, Kafka and WireMock.

### Last completed local verification

The MongoDB/MySQL implementation session on 2026-09-14 verified the Release build (zero warnings/errors), all eleven
packages and an isolated-cache package smoke test, with containers running on Windows x64/Podman. The recorded suite
results below are local evidence, not a remote CI or publication claim.

| Suite | Passed |
| --- | ---: |
| Core | 25 |
| Hosting | 22 |
| PostgreSQL | 16 |
| SQL Server | 14 |
| MongoDB | 13 |
| MySQL | 16 |
| Redis | 1 |
| Kafka | 8 |
| OrderService | 8 |
| **Total** | **123** |

One intentionally failing OrderService demonstration remains opt-in. Coverage depth is not uniform: the database
suites include dedicated real applications, while Redis and Kafka also rely on OrderService for application-level
composition coverage. Splitting projects did not automatically give every module the same acceptance coverage.

## Recommended module sequence

Database-first expansion and the sequence below are accepted. MongoDB and MySQL have landed in the working tree.
Detailed messaging, gRPC and cloud scope still needs the design/acceptance evidence described below.

| Milestone | Status | Why this comes next |
| --- | --- | --- |
| Lifecycle + SQL Server + test restructuring | Implemented | Establishes the foundation and a repeatable acceptance model |
| MongoDB | Implemented | Adds document-database coverage and tests which conventions generalize beyond SQL |
| MySQL | Implemented | Extends relational coverage using the existing behavioral contracts |
| RabbitMQ | Next module, after messaging hardening | Adds another messaging model; requires precise observation and processing guarantees |
| gRPC client | Proposed | Adds a new application boundary; keep dependency mocking a separate increment |
| Selected AWS/Azure services | Exploratory | Choose individual services and verify emulator behavior before committing scope |

This ranking reflects project fit and implementation scope, not measured adoption data. Reorder it when concrete user
demand warrants doing so. Testcontainers for .NET already provides MongoDB, MySQL and RabbitMQ modules, allowing Stove
to focus on configuration, lifecycle, test APIs and diagnostics. [Upstream module catalog](https://dotnet.testcontainers.org/modules/)

### Completed milestone: MongoDB

Implemented deliverables:

- A separate module package, named registration and test-context access.
- Managed container and existing-endpoint modes, typed configuration and readiness checks.
- Native MongoDB client/database/collection access with explicit ownership and client customization.
- Ordered setup for collections, indexes and seed data, plus explicit environment cleanup behavior.
- A small document-oriented test API where it improves assertions; retain native filters and serialization controls.
- A native-client application and its own acceptance project/CI entry proving application writes observed by Stove,
  Stove seeding read through the application, named databases, existing endpoints, cancellation and concurrent scopes.
- Failure-path tests, documentation, skill/API documentation synchronization and package smoke coverage.

The default is `mongo:8.0` with a single-node `stove-rs` replica set. Acceptance tests verify native transaction commit
and abort, optional standalone mode, BSON ObjectIds and field serialization. `Setup` is ordered collection/index/seed
preparation without a migration-history engine. The upstream [MongoDB module documentation](https://dotnet.testcontainers.org/modules/mongodb/) is an
implementation reference; the selected Testcontainers 4.15.0/MongoDB.Driver 3.11.2 combination is verified locally.

MongoDB has its own document-oriented suite and app; OrderService remains focused. The public API is `WithMongoDb`,
`t.MongoDb()`, native clients/collections and a small insert/query DSL. Sharding, hosted-service parity and broad chaos
tooling remain outside this increment. See the [database guide](docs/database-modules.md) for APIs and boundaries.

### Completed milestone: MySQL

- Implemented managed/existing modes, named instances, native client access, parameters, readiness, migrations and cleanup.
- Reused the relational behavior contract with an independent adapter and native-client application.
- Verified database creation, Unicode values, parameter handling and native connection-open callbacks.
- Selected MySqlConnector 2.6.2 and `mysql:8.4`; image overrides require their own compatibility checks.
- MariaDB compatibility is not claimed without a separate compatibility suite.

Extract shared production helpers only where the three relational implementations demonstrate real duplication.
There is no planned universal database interface or ORM dependency.

### Next work: messaging hardening, then RabbitMQ

First bound Kafka record retention without silently invalidating active assertions and define/test the uncorrelated-message
policy for sequential and overlapping scopes. Keep committed-offset semantics explicit. Then add RabbitMQ.
Proposed initial scope is topology setup, native client access, publishing,
dedicated observation queues, correlation, bounded waits and useful failure details. Verify routing and publisher
confirms separately from application processing. A Stove observer must not compete with the application for messages
on its work queue. Successful processing should be demonstrated through an application side effect or an explicitly
supported integration adapter, not inferred from publication alone.

### gRPC and cloud services

Start gRPC with generated clients, metadata/correlation, deadlines, cancellation, status and trailers against a real
application. Explicitly scope unary versus streaming support. A gRPC dependency mock belongs in a separate module or
increment, following the original Stove's client/mock separation.

For cloud support, the first candidates are Azure Blob Storage and AWS S3, followed by SQS/SNS or Azure Service Bus
according to demand. Prefer service-specific modules over an all-purpose AWS/Azure package. Emulator choice, API
coverage, authentication differences, licensing, platform support and parallel-test isolation need a feasibility
check before committing each service. Share emulator lifecycle internally only when the chosen services need it.

## Existing-module work that remains

| Work | Current gap | Scheduling recommendation |
| --- | --- | --- |
| Kafka record retention | The message store retains records for the environment lifetime | Complete before expanding messaging; bound storage without silently invalidating active assertions |
| Uncorrelated-message policy | Records without usable correlation can match every test, including later tests | Define strict/fallback behavior and compatibility; test sequential stale records and overlapping scopes |
| Processing semantics | Kafka committed offsets do not establish successful business processing | Keep documentation precise; design stronger adapters only for a concrete use case |
| Operation diagnostics | PostgreSQL, SQL Server and Redis have no dedicated failure-details provider | Add bounded per-test operation reports, with a deliberate policy for SQL, parameters and sensitive values |
| Redis acceptance depth | One dedicated test plus composition coverage | Add existing-service ownership, cleanup-failure and cancellation/readiness cases supported by the native client |
| Version/platform coverage | Local Podman success does not establish a full runtime/architecture matrix | Record tested combinations and add CI coverage where required by users |

MongoDB/MySQL landed without another core rewrite. These existing-module gaps remain visible work and are not counted
as solved by the test split or the new Kafka absence assertion.

## Completion criteria for each new module

1. Define its supported runtime modes, ownership, isolation and assertion guarantees.
2. Implement a separate package with public registration, configuration injection and native-client access.
3. Verify a real application through the public builder API, in an independent acceptance project.
4. Cover applicable lifecycle, existing-endpoint, cancellation, concurrency and failure cases from
   [module conventions](docs/modules.md). Explain any inapplicable contract rather than silently skipping it.
5. Add the suite to CI/release, compile the packaged API through the smoke test, and document tested runtime versions.
6. Update the README, module guide and matching agent-skill copies as needed; record limitations and verification.

A repository implementation, a passing local verification, a passing CI run and a published package are separate
states. Record release evidence when a release is made. No calendar estimates or NuGet release claims are assigned
by this roadmap.

## Deferred until a concrete need emerges

Container reuse and migration persistence, pause/resume or network chaos APIs, dependency-ordered startup, dashboards,
automatic database resets, ORM-specific integrations, and parity with every original Stove module are not prerequisites
for the next database modules. Revisit them with a concrete scenario and a focused design.

When a milestone lands, update its status, the current coverage and any decisions that changed. Keep implementation
details in the module guide and its tests; use this document for priorities and scope.
