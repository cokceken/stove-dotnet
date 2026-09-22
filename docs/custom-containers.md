# Bring your own dependency container

`StoveDotnet.Containers` integrates a Testcontainers container with Stove's dependency lifecycle. Use it for MinIO,
search engines, proprietary services or other images without a dedicated module. These are real Docker/Podman
containers, not in-memory emulations. The package depends only on Stove core and Testcontainers; it has no storage SDK.

Existing modules already have `Image` and `ConfigureContainer` options. Prefer those when the module's protocol and
client APIs fit. Generic containers provide lifecycle/configuration; service-specific setup and assertions stay with you.

## Registration

```csharp
using DotNet.Testcontainers.Builders;
using StoveDotnet.Containers;

builder.WithContainer("storage", o =>
{
    o.CreateContainer = () => new ContainerBuilder("quay.io/minio/minio:RELEASE.2025-09-07T16-13-09Z")
        .WithEnvironment("MINIO_ROOT_USER", accessKey)
        .WithEnvironment("MINIO_ROOT_PASSWORD", secretKey)
        .WithCommand("server", "/data")
        .WithPortBinding(9000, true)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r =>
            r.ForPort(9000).ForPath("/minio/health/ready")))
        .Build();

    o.InitializeAsync = async (container, ct) =>
    {
        // Construct your SDK client from container.Hostname and container.GetMappedPublicPort(9000).
        // Create a bucket or seed data. Dispose temporary clients and forward ct to I/O.
        await InitializeStorage(container, ct);
    };
    o.ConfigureExposedConfiguration = c =>
    [
        new("Storage:Endpoint", new UriBuilder("http", c.Container.Hostname,
            c.Container.GetMappedPublicPort(9000)).Uri.ToString()),
        new("Storage:AccessKey", accessKey), new("Storage:SecretKey", secretKey)
    ];
});
```

`InitializeStorage` is your callback, not a Stove API. The [complete fixture](../examples/CustomContainer/Tests/StorageTests.cs)
uses the MinIO SDK to create a bucket and maps the bucket name too. The image tag is a pinned example, not a default
chosen by the library. The factory can also return a container built by a specialized Testcontainers module.

Use native builder methods for environment variables, command/entrypoint arguments, files, ports, networks and
resource limits. Stove does not duplicate the Docker parameter model. See [native container configuration](https://dotnet.testcontainers.org/api/create_docker_container/).

## Readiness, configuration and ownership

The sequence is **create → start and native readiness → optional initialization → expose configuration → start applications**.
Always configure a service-specific wait strategy. A running process or open port alone might not mean the service
can handle requests. Use an HTTP health check, command, log condition or other appropriate
[Testcontainers wait strategy](https://dotnet.testcontainers.org/api/wait_strategies/). Set its timeout for your service.
The factory owns that choice; Stove does not inspect or infer the builder's readiness strategy.

Resolve host ports only after startup, in initialization or `ConfigureExposedConfiguration`. Use `Hostname` rather
than assuming localhost, and random host ports rather than fixed ports to support CI and concurrent fixtures.
The resulting host endpoint is suitable for Stove's in-process applications. Container-to-container connections need
explicit native networks/aliases and internal ports. Network/volume resources you create separately remain yours.

`CreateContainer` must return a fresh, unstarted instance. Stove owns its startup and disposal, including rollback
after readiness, initialization or application-start failures. Already-created/running instances are rejected without
taking ownership. Do not enable native resource reuse or disable cleanup if you expect Stove's ephemeral ownership
contract. Do not return the same instance from multiple factories. If a factory allocates resources and throws before
returning, it owns cleanup of those resources.

This increment does not adopt existing containers or manage shared external services. Use normal application
configuration or a dedicated module's `UseExisting` API for those. Images remain in the runtime cache after disposal;
external bind mounts/volumes can persist data. Containers are not automatically memory-backed storage.

Initialization receives the startup cancellation token; cancellation is cooperative. Dependencies start concurrently,
so registration order does not sequence two custom containers. Keep initialization local to its dependency; multi-container
orchestration graphs and Docker Compose are outside this API. Applications retain Stove's existing ordered startup and
reverse shutdown before dependencies are disposed.

## Test access and isolation

```csharp
await stove.Test(async t =>
{
    var native = t.Container("storage").Container;
    // Your SDK can connect through native.Hostname / native.GetMappedPublicPort(...).
    // Native operations receive t.CancellationToken; you own any SDK clients you create.
    var result = await native.ExecAsync(new[] { "some-service-command", "--check" }, t.CancellationToken);
    // Assert result with your framework.
});
```

`t.Container()` resolves the unnamed or only instance; multiple named instances without a default require a name.
Do not stop/dispose the native instance during other tests. Containers are fixture-owned, not reset per test, and
generic services do not automatically propagate Stove correlation. Use unique keys/buckets/indexes for parallel tests,
or independent containers when state must be reset. See [isolation recipes](../examples/Isolation/README.md).

## Failure output

Stove includes the container's name and state on test failure. Logs are off by default because arbitrary services can
print credentials. Opt in and redact project-specific data:

```csharp
o.IncludeLogs = true;
o.MaxLogLength = 4096; // default 8192 characters; zero disables retrieval
o.RedactLogs = text => text.Replace(secretKey, "[redacted]", StringComparison.Ordinal);
```

Redaction runs before truncation; a throwing redactor yields `[redaction failed]` without leaking its exception.
The excerpt contains the tail of combined stdout/stderr and is bounded by the configured character count plus a
truncation marker. These logs are shared container evidence, not attributed to the failing test. Native log retrieval
buffers the returned logs before truncation; leave logs disabled for very noisy services. Stove does not print environment
variables, commands or application configuration in this section. This policy does not sanitize native startup exceptions,
application logs, SDK errors or logging configured directly on the Testcontainers builder.

## Executable evidence

```shell
dotnet test --project tests/StoveDotnet.Containers.AcceptanceTests -c Release
dotnet test --project examples/CustomContainer/Tests -c Release
```

Both run in CI with a Docker-compatible runtime. The acceptance suite covers named instances, native commands/files,
readiness failure, cancellation, initialization rollback, disposal, ownership rejection and bounded/redacted diagnostics.
The object-storage example proves configuration and bucket initialization through a real API and SDK.
