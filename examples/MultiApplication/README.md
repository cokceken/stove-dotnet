# API and worker with shared dependencies

This example starts the [API](Api/Program.cs) and [worker](Worker/WorkerApplication.cs) as separate named in-process
hosts, sharing a Testcontainers PostgreSQL database and RabbitMQ broker. The worker is an independent executable;
its production entry point and tests call the same factory. Neither application depends on Stove.

```shell
dotnet test --project examples/MultiApplication/Tests -c Release
```

Run from the repository root with .NET 10 and Docker or Podman available. The [fixture and tests](Tests/OrderTests.cs)
prepare schema/topology, start the worker before the API, and bind `t.Http("api")` to the API's assigned Kestrel port.
Two tests cover a complete request and four overlapping requests with distinct order IDs:

`POST /orders → RabbitMQ → worker → PostgreSQL → GET /orders/{id}`

The broker assertion verifies correlation and publication. The bounded HTTP assertion separately proves that the
worker persisted the result. Shutdown stops the API and worker before releasing the shared dependencies.
This is an adoption example, not a production broker reliability implementation: retry/dead-letter policy,
authentication, schema migration history and an outbox are deliberately outside its scope.

See [multiple-application adoption](../../docs/multiple-applications.md) for configuration, readiness, service access,
failure logs and the limitations of running multiple hosts inside one process.
