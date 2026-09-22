# Isolation recipes backed by executable tests

| Strategy | Runnable reference | Parallel execution |
| --- | --- | --- |
| Shared API, worker, PostgreSQL and broker; unique IDs and correlated messages | [OrderTests](../MultiApplication/Tests/OrderTests.cs) | Yes, when application behavior is keyed and tests avoid global resets or configuration changes. |
| Fresh database/container, broker and hosts per scenario; even the same business key may be reused | [IsolatedEnvironmentTests](../MultiApplication/Tests/IsolatedEnvironmentTests.cs) | Yes, at greater startup/resource cost. Each environment disposes its own hosts before its dependencies. |
| API and worker share one fixture-owned clock | [TimeTests](../../tests/StoveDotnet.Time.AcceptanceTests/TimeTests.cs) | Serialize all tests advancing that clock; unique row IDs alone do not isolate time. |
| Separate fixtures own separate clocks and hosts | [TimeTests](../../tests/StoveDotnet.Time.AcceptanceTests/TimeTests.cs) | Yes; the suite advances two isolated environments concurrently. |
| Shared mutable clock across test classes | [SharedClockTests](../../tests/StoveDotnet.Time.AcceptanceTests/SharedClockTests.cs) | Explicitly serialized through an xUnit collection. Use equivalent fixture/scheduling rules with other frameworks. |

```shell
dotnet test --project examples/MultiApplication/Tests -c Release
dotnet test --project tests/StoveDotnet.Time.AcceptanceTests -c Release
```

The first command requires Docker or Podman; the clock tests run with real in-process Kestrel and Generic Hosts.
Both are in CI. The shared example verifies API publication **and** worker persistence read through HTTP. The isolated
example starts two complete environments at once and verifies different values for the same order ID.

Choose unique IDs for rows, cache keys and messages; verify only work owned by the scenario. Dedicated consumer queues
prevent test observers from competing with workers. Never truncate shared tables or reset shared stubs while another
test is active. Waiting for an HTTP response is not sufficient if a worker still owns queued/in-flight work: first observe
the business outcome. During teardown, stop hosts/workers before deleting their dependencies. On external databases,
the fixture must own its chosen database/schema and cleanup policy; a separate Stove instance does not isolate a reused
external endpoint by itself.

Transactions opened by test code apply to that connection. The API and worker have other connections and commit
independently, so their writes are not rolled back by the test's transaction. Use unique data, owned databases or serialize
reset operations. Separate named hosts also share process statics and environment variables; avoid mutating these in
parallel fixtures. See [multiple applications](../../docs/multiple-applications.md) for those limits and
[practical testing](../../docs/practical-testing.md) for waits, redaction, clock ownership and OIDC setup.
