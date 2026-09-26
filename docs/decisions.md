# Architecture and product decisions

Recorded: 2026-09-14. These decisions summarize the agreed direction from the module review and test restructuring.
Implementation status and proposed future milestones live in the [roadmap](../ROADMAP.md). Detailed module authoring
requirements live in [module conventions](modules.md).

| ID | Accepted decision | Reason and consequence |
| --- | --- | --- |
| D01 | Keep the current capability-based module architecture | Small lifecycle interfaces, named registration and typed configuration are sufficient for incremental expansion; no module-system rewrite is planned |
| D02 | Run real applications against real dependencies | Application projects use their native production clients and contain no Stove dependency; configuration points them to the test environment |
| D03 | Keep modules independently consumable | Each provider has its own package and acceptance project; adding MongoDB must not pull it into SQL Server tests or every example |
| D04 | Preserve native APIs | Provider-specific parameters, filters and clients remain accessible; shared helpers should not erase useful database or protocol behavior |
| D05 | Separate test responsibilities | Core tests verify orchestration; module suites verify provider behavior; real-app contracts verify public setup; OrderService demonstrates composition; package smoke tests verify distribution |
| D06 | Share contracts without coupling providers | The shared testing project has no database-driver dependencies; each provider owns its adapter, application and runtime fixture |
| D07 | Keep OrderService focused | It demonstrates PostgreSQL, Redis, Kafka and external HTTP interactions; it is not a registry of every available module |
| D08 | Make isolation explicit | A named instance is not a per-test reset; correlation does not isolate rows, and a test-owned transaction does not include the app's separate connections |
| D09 | Preserve failures while releasing resources | Cleanup failure must not skip later disposal; rollback waits for starts already in flight and retains startup/rollback errors |
| D10 | State messaging guarantees precisely | Publication, routing, offset commits and successful business processing are different observations; negative assertions describe a bounded observation window |
| D11 | Use independent CI suite jobs | A failing provider must not suppress other suite results; packaging and release wait for the suite matrix |
| D12 | Expand database coverage first | SQL Server, MongoDB and MySQL are implemented; messaging/protocol/cloud follow with explicit scope |
| D13 | MongoDB uses native document APIs and explicit database selection | Default `mongo:8.0` single-node replica set supports verified native transactions; standalone is optional. Ordered `Setup` manages collections/indexes/seeds without a migration-history engine |
| D14 | MySQL uses MySqlConnector and the relational acceptance contract | Default `mysql:8.4` is tested with MySqlConnector 2.6.2; callbacks and parameters remain native; MariaDB parity is not claimed |
| D15 | Bound broker evidence per test and require correlation by default | Kafka/RabbitMQ share count/byte limits; overflow invalidates assertions. Headerless fallback is opt-in and limited to one active scope; arrival time cannot identify delayed old work |
| D16 | RabbitMQ observes dedicated queues and exposes native confirms | Bind an exclusive queue to named exchanges; never compete for application messages. Keep publisher confirms, routing and application processing distinct; fail on observer loss rather than hide gaps with recovery |
| D17 | Azure is a package family; Service Bus is independently consumable | `StoveDotnet.Azure.ServiceBus` owns its SDK/emulator dependencies; a future `StoveDotnet.Azure` may be a convenience meta-package |
| D18 | Service Bus separates broker state, destructive receive and business processing | Peek scheduled state without waiting; explicit receive settlement may compete, and successful processing requires a business-visible assertion |

## What the original Stove informs

### Practical consumer evidence: RailSense

RailSense's response/upload helpers, waits, clock and fake OIDC server informed the reusable features in
[practical testing](practical-testing.md). The consumer was inspected without modification. Accepted decisions:

- Extend HTTP wrappers with fluent status checks and configurable bounded/redacted diagnostics; retain raw access
  and lazy deserialization. Raw response data remains caller-controlled.
- Preserve legacy retry behavior and add token-aware overloads with explicit retry selection. Await cooperative probes
  sequentially; never abandon timed-out work and start overlapping probes. Absence sampling has a bounded, documented scope.
- Use Microsoft's `FakeTimeProvider` in an optional package. Fixtures explicitly own clocks and register them per app;
  avoid a second timer scheduler. External database and token-validation clocks remain independent.
- Reuse named applications for shared unique-data and independent environment examples. No implicit transaction/reset API.
- Keep the optional OIDC module generic: local discovery/JWKS and signed token scenarios verified through JWT bearer.
  Domain roles and interactive identity-provider flows stay outside this module.

Named in-process applications are now supported. APIs and Generic Host workers keep separate configuration and
lifetimes while sharing dependencies. Registration order controls startup/readiness; reverse order controls shutdown.
HTTP clients explicitly target applications when no default exists. Core stays independent of Microsoft hosting APIs;
`StoveDotnet.Hosting` provides worker lifecycle and application-local log collection. Separate-process hosting and
global instrumentation isolation remain outside this increment. See [the adoption guide](multiple-applications.md).

Framework adoption now has executable evidence: separate xUnit v3, NUnit, MSTest and TUnit examples share
only application/environment setup. CI checks native assertions, cancellation, runner failure/skip outcomes,
filtering and teardown on .NET 10/MTP, then repeats against isolated package consumers. The core remains free
of framework dependencies. The immediate next step is a real consumer pilot; dashboard integration is deferred
while existing failure output serves diagnosis. See the [adopter guide](test-frameworks.md).

The local original-Stove checkout was reviewed during the initial assessment. Useful references include structured
operation reporting, explicit container/provided runtime capabilities, migration behavior and separate gRPC client/mock
modules. Its Spring Kafka integration can observe consumer outcomes directly, whereas StoveDotnet's current Kafka
consumption assertion uses committed offsets.

The decision is to borrow useful capabilities and behavior, not reproduce JVM-specific abstractions or claim feature
parity. Broader operation reporting, container controls, reuse and processing adapters remain future work.

## Accepted direction versus unresolved design

Generic dependency containers are supported through the optional `StoveDotnet.Containers` package. Consumers supply a
fresh Testcontainers factory with native readiness and optional initialization; Stove owns startup, configuration mapping,
failure evidence and disposal. Native builder options are reused rather than mirrored into a second Docker API.
Dedicated modules remain useful for provider clients and testing semantics. Existing-container adoption, resource reuse,
dependency graphs and Compose orchestration are outside this increment. A real MinIO/API example validates the boundary.

The database-first direction and the architecture/testing rules above are accepted. MongoDB's API/topology/transaction
scope and MySQL's driver/version policy are implemented (D13/D14). Broker retention/correlation and RabbitMQ observation
are now implemented (D15/D16). Azure Service Bus is implemented under D17/D18. gRPC streaming scope and other cloud
emulator choices are not finalized. The roadmap proposes a sequence and initial boundaries for those decisions. They
should be resolved with their module's implementation and acceptance evidence.

When changing an accepted decision, record the new requirement, the tradeoff and which earlier decision it replaces.
Do not silently rewrite an implemented limitation as a guaranteed capability.
