# Project roadmap

Updated: 2026-09-26. Original baseline: `09e4a04` (`individual tests for modules`); database expansion, messaging hardening, Azure Service Bus, framework adoption and practical RailSense-driven testing improvements are implemented.

StoveDotnet's immediate direction is adoption in a real consumer project, using contract-focused end-to-end tests
to identify practical gaps. Framework examples now provide a starting point. Broader module coverage remains on the
roadmap; prioritize the dependencies that the pilot actually needs. Each addition should include a useful testing
experience, a real application acceptance suite and a consumable package. A dashboard is not a current priority.

This document separates implemented work from accepted decisions and proposed future scope. **Implemented** means
present in the repository; it does not imply a particular NuGet release or a successful remote CI run. Future ordering
is a planning recommendation, not a delivery-date commitment. See [architecture decisions](docs/decisions.md) for the
reasoning and [module conventions](docs/modules.md) for implementation requirements.

## Implemented baseline

| Area | Present behavior |
| --- | --- |
| Core | Small lifecycle capabilities, named instances, typed configuration injection, migrations, cancellation, test correlation and failure wrapping |
| Application hosting | Named ASP.NET Core and Generic Host applications, per-app configuration/readiness, targeted HTTP clients, named service access and correlated application logs; API/worker composition example |
| Dependencies | PostgreSQL, SQL Server, MongoDB, MySQL, Redis, Kafka, RabbitMQ and named in-process WireMock servers |
| Telemetry | OTLP traces/logs, test correlation and failure details |
| Lifecycle hardening | Attempt all disposal steps despite cleanup failures; retain startup and rollback errors; wait for in-flight starts before rollback |
| Databases | Managed and existing endpoints, native clients, migration ordering, readiness before application startup for PostgreSQL/SQL Server/MySQL; MongoDB has ordered collection/index/seed setup and native transactions, native-client customization |
| Broker observation | Kafka/RabbitMQ retain bounded per-test evidence, strict default correlation, explicit overflow failure and scope cleanup |
| Kafka absence assertions | `ShouldNotBePublished` observes for an explicit interval; it does not prove absence beyond that interval or through observer lag |
| Test organization | Core-only unit tests; separate hosting, PostgreSQL, SQL Server, MongoDB, MySQL, Redis, Kafka and RabbitMQ suites; provider-neutral database contracts |
| Real test applications | Hosting, PostgreSQL, SQL Server, MongoDB, MySQL and RabbitMQ apps under `tests/TestApps`, with no Stove dependency |
| Composition example | OrderService retains PostgreSQL, Redis, Kafka and external HTTP dependencies; eight active tests cover workflows, retrieval, rejection, concurrency and diagnostics |
| Framework adoption | Separate xUnit v3, NUnit, MSTest and TUnit examples on .NET 10/MTP, five tests each; runner failure/skip/filter checks and teardown audits; isolated package-consumer verification |
| CI and packages | Shared CI/release matrix with separate suite jobs, fail-fast disabled, packaging gated on tests and an isolated package smoke test |
| HTTP adoption | Fluent expected status, bounded/redacted diagnostics, correlated hand-built request wrappers and lazy typed responses |
| Waiting | Effective deadline tokens, configurable retry, value diagnostics and sequential bounded throughout sampling; legacy retry behavior retained |
| Test time | Optional Microsoft FakeTimeProvider integration with explicit fixture ownership, timers and shared/independent host examples |
| Identity | Optional generic OIDC discovery/JWKS and configurable signed/invalid tokens, tested through real JWT bearer authentication |
| Isolation | Shared unique-data tests, concurrent independently owned API/worker/database/broker environments and serialized shared-clock fixtures |

SQL Server is implemented; it is no longer a future-module candidate. The repository contains sixteen library packages:
core, ASP.NET Core, Generic Host hosting, HTTP, telemetry, PostgreSQL, SQL Server, MongoDB, MySQL, Redis, Kafka, RabbitMQ,
WireMock, Time, OIDC and Containers. See [practical testing](docs/practical-testing.md) for consumer APIs and compatibility decisions.
Generic container support now covers user-supplied images, native readiness, initialization, exposed configuration and
owned cleanup, with a real MinIO/API example. See [custom containers](docs/custom-containers.md). Dedicated modules
can be prioritized for specialized testing APIs rather than basic image startup.

### Local verification history

Custom containers (2026-09-22): Release solution build passed with zero warnings/errors. Six container acceptance tests,
three MinIO/API example tests, 46 core tests and 35 hosting tests passed on Windows/Podman (90 total). Sixteen packages
were packed locally as `0.1.0-preview.2.5`. The smoke script's consumer ran from a fresh directory/cache through PowerShell;
an additional package-only runtime check started a custom container, executed a native command and disposed it.
Both new suites are in the shared CI matrix, documentation links resolve and skill copies match. No packages were published.

Practical consumer improvements (2026-09-22): Release solution build passed with zero warnings/errors on Windows/Podman.
The suites passed 203 tests: core 46, hosting/HTTP 35, Time 5, OIDC 14, PostgreSQL 16, SQL Server 14, MongoDB 13,
MySQL 16, Redis 1, Kafka 11, RabbitMQ 21, API/worker isolation 3 and OrderService 8. The intentionally failing
OrderService demonstration remained skipped. Fifteen packages were packed locally as `0.1.0-preview.2.4`.
The smoke script's standalone consumer was extracted and run through PowerShell with a fresh NuGet cache; restore,
compilation, WireMock/Generic Host runtime, fake timers and OIDC discovery/token issuance passed.
All four framework package consumers passed full (20 tests), filtered, expected-failure and skip/cleanup checks.
The 19 runner-output parser checks passed, guide links resolve and skill copies match. No remote CI, release or
publication is claimed. RailSense was inspected only. Remaining limits are documented in [practical testing](docs/practical-testing.md).

Named-host adoption verification: Release solution build with zero warnings/errors; 37 core tests, 31 hosting tests,
two API/worker tests and eight existing OrderService tests passed locally on Windows/Podman. Thirteen packages were
packed, and the isolated package smoke check started a named Generic Host. The four framework package-consumer suites
also passed their full, filtered, expected-failure and skip/cleanup checks. These are local results, not a remote CI
or publication claim. See [multiple applications](docs/multiple-applications.md) for scope and limitations.

The framework adoption work adds twenty passing example tests on Windows/Podman, plus four filtered runs,
four expected-failure runs and four skip/inconclusive runs with cleanup checks. See the
[framework guide](docs/test-frameworks.md) and [verification script](scripts/verify-framework-examples.ps1).
The same sixteen runner processes passed against locally packed `0.1.0-preview.2` packages from a fresh
consumer directory and NuGet cache. The full Release build passed with zero warnings/errors. No remote CI
run or package publication is claimed by this verification.
This work does not claim verification of every IDE or VSTest adapter combination.

The messaging implementation session on 2026-09-14 verified the Release build (zero warnings/errors), all twelve
packages and an isolated-cache package smoke test, with containers running on Windows x64/Podman. The recorded suite
results below are local evidence, not a remote CI or publication claim.

| Suite | Passed |
| --- | ---: |
| Core | 32 |
| Hosting | 22 |
| PostgreSQL | 16 |
| SQL Server | 14 |
| MongoDB | 13 |
| MySQL | 16 |
| Redis | 1 |
| Kafka | 11 |
| RabbitMQ | 21 |
| OrderService | 8 |
| **Total** | **154** |

One intentionally failing OrderService demonstration remains opt-in. Coverage depth is not uniform: the database
suites include dedicated real applications, while Redis and Kafka also rely on OrderService for application-level
composition coverage. Splitting projects did not automatically give every module the same acceptance coverage.

## Recommended module sequence

Database-first expansion and the sequence below are accepted. MongoDB, MySQL, Kafka retention/correlation hardening,
RabbitMQ and Azure Service Bus are implemented. gRPC and other cloud services still need design/acceptance evidence.

| Milestone | Status | Why this comes next |
| --- | --- | --- |
| Lifecycle + SQL Server + test restructuring | Implemented | Establishes the foundation and a repeatable acceptance model |
| MongoDB | Implemented | Adds document-database coverage and tests which conventions generalize beyond SQL |
| MySQL | Implemented | Extends relational coverage using the existing behavioral contracts |
| Kafka retention/correlation | Implemented | Bounded per-test evidence, strict default matching and explicit incomplete-observation failures |
| RabbitMQ | Implemented | Adds another messaging model; requires precise observation and processing guarantees |
| gRPC client | Next proposed module | Adds a new application boundary; keep dependency mocking a separate increment |
| Azure Service Bus | Implemented | Typed queues/topics/subscriptions, scheduling, explicit settlement and TokenCredential support |
| Other selected AWS/Azure services | Exploratory | Choose individual services and verify emulator behavior before committing scope |

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

### Completed milestone: Kafka hardening and RabbitMQ

Kafka and RabbitMQ now share an internal scoped observation buffer. Defaults are 10,000 messages / 16 MiB per active
test, with explicit failure on overflow. Successful scopes release evidence; failed scopes preserve bounded diagnostics
without an environment-lifetime archive. Strict matching requires a matching test id or valid traceparent; when both
are present they must agree. Missing headers require the explicit `SingleActiveTest` fallback. Its arrival-time
limitation is documented, including old Kafka records received while catching up. This intentionally changes Kafka's
previous permissive, unbounded behavior; see [migration guidance](docs/messaging.md).

RabbitMQ adds a separate package and native-client application suite. Ordered setup completes before hosting;
Stove observes an exclusive queue bound to explicit named exchanges, leaving application work queues alone. Publishing
awaits confirms and defaults to mandatory routing. Tests distinguish unroutable returns, routes reaching only the
observer, application success/rejection, observer cancellation/loss, existing-server ownership and failure cleanup.
Automatic recovery is disabled to avoid hiding observation gaps. Native connection/channel access remains available.
No `ShouldBeConsumed` processing inference, cluster recovery or stream-protocol support is claimed.

### gRPC and cloud services

Start gRPC with generated clients, metadata/correlation, deadlines, cancellation, status and trailers against a real
application. Explicitly scope unary versus streaming support. A gRPC dependency mock belongs in a separate module or
increment, following the original Stove's client/mock separation.

Azure Service Bus is implemented as the first `StoveDotnet.Azure.*` service package. It supports the official emulator,
typed queues/topics/subscriptions/rules, existing connection strings, existing namespaces with `TokenCredential`,
correlated publish/schedule/peek and explicit destructive receive settlement. Scheduled assertions inspect broker state
without waiting and cancel discovered test schedules at scope end. The emulator cannot verify Entra/RBAC behavior and
is not universally parallel-safe; see [the module guide](docs/azure-service-bus.md).

Future candidates include Azure Blob Storage, AWS S3 and SQS/SNS. Prefer service-specific modules over an all-purpose
AWS/Azure binary; a convenience `StoveDotnet.Azure` meta-package may reference independent Azure service packages.
Emulator choice, API coverage, authentication differences, licensing, platform support and parallel-test isolation
need a feasibility check before committing each service. Share emulator lifecycle internally only when demonstrated.

## Existing-module work that remains

| Work | Current gap | Scheduling recommendation |
| --- | --- | --- |
| Processing semantics | Kafka committed offsets do not establish successful business processing | Keep documentation precise; design stronger adapters only for a concrete use case |
| Operation diagnostics | PostgreSQL, SQL Server and Redis have no dedicated failure-details provider | Add bounded per-test operation reports, with a deliberate policy for SQL, parameters and sensitive values |
| Redis acceptance depth | One dedicated test plus composition coverage | Add existing-service ownership, cleanup-failure and cancellation/readiness cases supported by the native client |
| Version/platform coverage | Local Podman success does not establish a full runtime/architecture matrix | Record tested combinations and add CI coverage where required by users |

Database and messaging expansion landed without a core lifecycle rewrite. The remaining gaps above are separate from
the completed retention/correlation hardening. Kafka transient observer lag and broader fault-injection coverage also
remain limitations; bounded absence assertions do not establish broker-wide absence.

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
