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
| D12 | Expand database coverage next | SQL Server is implemented; MongoDB and MySQL are the next recommended additions, followed by messaging/protocol/cloud work with explicit scope |

## What the original Stove informs

The local original-Stove checkout was reviewed during the initial assessment. Useful references include structured
operation reporting, explicit container/provided runtime capabilities, migration behavior and separate gRPC client/mock
modules. Its Spring Kafka integration can observe consumer outcomes directly, whereas StoveDotnet's current Kafka
consumption assertion uses committed offsets.

The decision is to borrow useful capabilities and behavior, not reproduce JVM-specific abstractions or claim feature
parity. Broader operation reporting, container controls, reuse and processing adapters remain future work.

## Accepted direction versus unresolved design

The database-first direction and the architecture/testing rules above are accepted. MongoDB's exact API, default
topology and transaction scope; MySQL's driver/version policy; RabbitMQ's observation design; gRPC streaming scope;
and cloud emulator choices are not finalized. The roadmap proposes a sequence and initial boundaries for those
decisions. They should be resolved with their module's implementation and acceptance evidence.

When changing an accepted decision, record the new requirement, the tradeoff and which earlier decision it replaces.
Do not silently rewrite an implemented limitation as a guaranteed capability.
