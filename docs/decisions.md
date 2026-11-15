# Architecture and product decisions

What is planned next lives in the [roadmap](https://github.com/cokceken/stove-dotnet/blob/main/ROADMAP.md). Detailed module authoring
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
| D19 | HTTP responses offer fluent status checks with bounded, redacted diagnostics | Raw access and lazy deserialization stay; raw response data remains caller-controlled |
| D20 | One retry rule per method | `AssertAsync` retries every exception; `UntilAsync` polls a value and lets exceptions propagate; `RetryOn` overrides either. Probes are awaited sequentially, never abandoned or overlapped. Replaces an earlier decision to keep a separate legacy retry behavior |
| D21 | Test time uses Microsoft's `FakeTimeProvider`, owned by the fixture | No second timer scheduler; fixtures register a clock per application. External database and token-validation clocks stay independent |
| D22 | The OIDC module stays generic | Local discovery/JWKS and signed-token scenarios verified through real JWT bearer validation; domain roles and interactive identity-provider flows are out of scope |
| D23 | Applications are named and run in-process | APIs and Generic Host workers keep separate configuration and lifetimes while sharing dependencies; registration order is startup order and shutdown reverses it. Separate-process hosting and per-application instrumentation isolation are out of scope |
| D24 | Core has no test-framework dependency | xUnit v3, NUnit, MSTest and TUnit examples share only application and environment setup; CI verifies runner outcomes and teardown for each |
| D25 | Custom containers reuse native Testcontainers builders | Stove owns startup, configuration mapping, failure evidence and disposal, and adds no second Docker API. Adopting existing containers, resource reuse and Compose orchestration are out of scope |
| D26 | Systems declare startup prerequisites through `ISystemDependencies` | Independent systems still start in parallel; shutdown reverses the dependency order |
| D27 | Borrow capabilities from the original Stove, not its shape | Useful references are structured operation reporting, container/provided runtime modes, migrations and separate gRPC client/mock modules. JVM-specific abstractions and feature parity are not goals |
| D28 | Failure evidence needs no application cooperation | A failed test reports the requests it sent and the application's in-process logs whichever assertion library failed; telemetry adds the trace tree |
| D29 | Startup failures name what failed | `StoveStartupException` names the system or application, repeats the cause and lists every system that failed in a parallel start |

## Open questions

gRPC streaming scope and the choice of emulators for further cloud services are not decided. Resolve them with the
module's implementation and acceptance evidence; the [roadmap](https://github.com/cokceken/stove-dotnet/blob/main/ROADMAP.md)
proposes the initial boundaries.

## Changing a decision

Record the new requirement, the tradeoff and which earlier decision it replaces. Do not silently rewrite an
implemented limitation as a guaranteed capability.
