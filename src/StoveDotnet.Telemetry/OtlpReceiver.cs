using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using Google.Protobuf;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Proto.Collector.Logs.V1;
using OpenTelemetry.Proto.Collector.Trace.V1;

namespace StoveDotnet.Telemetry;

/// <summary>Minimal OTLP/HTTP (protobuf) receiver for traces and logs.</summary>
internal sealed class OtlpReceiver(TelemetryStore store, int port) : IAsyncDisposable
{
    private const string ProtobufContentType = "application/x-protobuf";
    private WebApplication? _app;

    public Uri Endpoint { get; private set; } = null!;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, port));

        var app = builder.Build();
        app.Use((context, next) =>
        {
            // The application under test runs in this process and its instrumentation listens process-wide.
            // Mark the receiver's own request activity as not recorded so exports do not produce new spans to export.
            if (Activity.Current is { } activity)
            {
                activity.IsAllDataRequested = false;
                activity.ActivityTraceFlags &= ~ActivityTraceFlags.Recorded;
            }

            return next(context);
        });

        app.MapPost("/v1/traces", context => Handle(context, ExportTraceServiceRequest.Parser, request =>
        {
            store.Add(OtlpConverter.ToSpans(request));
            return new ExportTraceServiceResponse();
        }));
        app.MapPost("/v1/logs", context => Handle(context, ExportLogsServiceRequest.Parser, request =>
        {
            store.Add(OtlpConverter.ToLogs(request));
            return new ExportLogsServiceResponse();
        }));

        // Metrics are not collected; accept them so exporters in the application do not log errors.
        app.MapPost("/v1/metrics", context =>
        {
            context.Response.StatusCode = StatusCodes.Status200OK;
            context.Response.ContentType = ProtobufContentType;
            return Task.CompletedTask;
        });

        await app.StartAsync(cancellationToken).ConfigureAwait(false);
        _app = app;

        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        Endpoint = new Uri(address);
    }

    private static async Task Handle<TRequest, TResponse>(
        HttpContext context,
        MessageParser<TRequest> parser,
        Func<TRequest, TResponse> handle)
        where TRequest : IMessage<TRequest>
        where TResponse : IMessage<TResponse>
    {
        if (!string.Equals(context.Request.ContentType?.Split(';')[0].Trim(), ProtobufContentType, StringComparison.OrdinalIgnoreCase))
        {
            context.Response.StatusCode = StatusCodes.Status415UnsupportedMediaType;
            return;
        }

        var body = context.Request.Body;
        if (string.Equals(context.Request.Headers.ContentEncoding, "gzip", StringComparison.OrdinalIgnoreCase))
        {
            body = new GZipStream(body, CompressionMode.Decompress);
        }

        using var buffer = new MemoryStream();
        await body.CopyToAsync(buffer, context.RequestAborted).ConfigureAwait(false);
        buffer.Position = 0;

        TRequest request;
        try
        {
            request = parser.ParseFrom(buffer);
        }
        catch (InvalidProtocolBufferException)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        var response = handle(request);
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = ProtobufContentType;
        await context.Response.Body.WriteAsync(response.ToByteArray(), context.RequestAborted).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.StopAsync().ConfigureAwait(false);
            await _app.DisposeAsync().ConfigureAwait(false);
        }
    }
}
