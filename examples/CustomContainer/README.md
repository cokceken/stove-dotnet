# A custom MinIO container behind a real API

This example has no MinIO-specific Stove module. The [fixture](Tests/StorageTests.cs) configures a native Testcontainers
builder with a pinned MinIO image, credentials, command, random port and HTTP readiness. It creates a bucket through
the MinIO SDK, then maps endpoint/credentials/bucket into the [API](Api/Program.cs). The API depends only on MinIO's SDK.

```shell
dotnet test --project examples/CustomContainer/Tests -c Release
```

Requires .NET 10 and Docker or Podman. MinIO withdrew its official container images in September 2026, so the example
uses the source-built `ghcr.io/coollabsio/minio` mirror of the final community security release, pinned by digest.
Substitute an image you build or trust from your own registry as needed. This compatibility example is not a production
storage deployment recommendation. The generic library does not choose a storage vendor or image.

The API refuses startup unless the bucket already exists. Tests upload and download through HTTP, check missing-object
behavior, and overlap scenarios using distinct object keys. This exercises **HTTP → API → MinIO SDK → real container**.
The shared fixture keeps its objects until teardown. No per-test reset or implicit data isolation is promised.

Example credentials are test-only. Real credentials and image-specific conventions belong to the consumer fixture.
Stove disposes the API before the container. Temporary storage clients in initialization are disposed by the callback;
the API owns its singleton client. See [custom-container adoption](../../docs/custom-containers.md) for native access,
readiness, lifecycle, diagnostic redaction and limitations.
