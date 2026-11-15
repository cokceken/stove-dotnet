# Project roadmap

This document holds what is planned and what is deliberately deferred. What already exists is described by the
[README](README.md) and the [documentation](https://cokceken.github.io/stove-dotnet/); what shipped when is in the
release notes and git history. See [architecture decisions](docs/decisions.md) for the reasoning behind the design and
[module conventions](docs/modules.md) for implementation requirements.

The immediate direction is adoption in a real consumer project, using contract-focused end-to-end tests to find
practical gaps. Prioritize the dependencies that pilot actually needs. Each addition should include a useful testing
experience, a real-application acceptance suite and a consumable package. A dashboard is not a priority.

Ordering here is a planning recommendation, not a delivery-date commitment. It reflects project fit and implementation
scope, not measured adoption data; reorder it when concrete demand warrants.

| Candidate | Status | Why |
| --- | --- | --- |
| gRPC client | Next proposed module | Adds a new application boundary; keep dependency mocking a separate increment |
| Other selected AWS/Azure services | Exploratory | Choose individual services and verify emulator behavior before committing scope |

## Next modules

Start gRPC with generated clients, metadata/correlation, deadlines, cancellation, status and trailers against a real
application. Explicitly scope unary versus streaming support. A gRPC dependency mock belongs in a separate module or
increment, following the original Stove's client/mock separation.

Future candidates include Azure Blob Storage, AWS S3 and SQS/SNS. Prefer service-specific modules over an all-purpose
AWS/Azure binary; a convenience `StoveDotnet.Azure` meta-package may reference independent Azure service packages.
Emulator choice, API coverage, authentication differences, licensing, platform support and parallel-test isolation
need a feasibility check before committing each service. Share emulator lifecycle internally only when demonstrated.

## Work remaining in existing modules

| Work | Current gap | Recommendation |
| --- | --- | --- |
| Unmapped configuration | A system whose `ConfigureExposedConfiguration` maps nothing starts silently, and the application keeps the connection details from its own settings | Fail or warn at startup, with an explicit opt-out for systems only the test uses |
| WireMock correlation | Requests without correlation headers are visible to every test, while the brokers exclude uncorrelated data by default | Offer the brokers' strict policy for WireMock, or document why HTTP differs |
| WireMock request log | The log of each WireMock server grows for the whole run and is scanned on every read. WireMock.Net drops incoming requests from the log when entries are removed while requests arrive, so Stove does not trim it | Revisit if WireMock.Net makes removal safe, or keep a per-test index beside the log |
| Operation diagnostics | PostgreSQL, SQL Server, MongoDB, MySQL and Redis contribute nothing to a failure report | Add bounded per-test operation reports, with a deliberate policy for SQL, parameters and sensitive values |
| Processing semantics | Kafka committed offsets do not establish successful business processing | Keep documentation precise; design stronger adapters only for a concrete use case |
| Redis acceptance depth | One dedicated test plus composition coverage | Add existing-service ownership, cleanup-failure and cancellation/readiness cases |
| Version/platform coverage | CI runs Linux x64 only | Record tested combinations and add CI coverage where users need it |

Kafka transient observer lag and broader fault-injection coverage also remain limitations; bounded absence assertions
do not establish broker-wide absence.

## Completion criteria for each new module

1. Define its supported runtime modes, ownership, isolation and assertion guarantees.
2. Implement a separate package with public registration, configuration injection and native-client access.
3. Verify a real application through the public builder API, in an independent acceptance project.
4. Cover applicable lifecycle, existing-endpoint, cancellation, concurrency and failure cases from
   [module conventions](docs/modules.md). Explain any inapplicable contract rather than silently skipping it.
5. Add the suite to CI/release, compile the packaged API through the smoke test, and document tested runtime versions.
6. Update the README, module guide and agent skill as needed; record limitations and verification.

A repository implementation, a passing local verification, a passing CI run and a published package are separate
states. Record release evidence when a release is made. No calendar estimates or NuGet release claims are assigned
by this roadmap.

## Deferred until a concrete need emerges

Container reuse and migration persistence, pause/resume or network chaos APIs, dashboards,
automatic database resets, ORM-specific integrations, and parity with every original Stove module are not prerequisites
for the next modules. Revisit them with a concrete scenario and a focused design.

When a milestone lands, remove it from this document and record what shipped in the release notes. Keep
implementation details in the module guide and its tests; use this document for priorities and scope.
