using Microsoft.Extensions.DependencyInjection.Extensions;

var builder = WebApplication.CreateBuilder(args);
builder.Services.TryAddSingleton(TimeProvider.System);
builder.Services.AddSingleton<TimerWorker>();
builder.Services.AddHostedService(s => s.GetRequiredService<TimerWorker>());
var app = builder.Build();
app.MapGet("/clock", (TimeProvider clock, TimerWorker worker) => new { now = clock.GetUtcNow(), ticks = worker.Ticks, waiting = worker.Waiting });
app.MapPost("/delay", async (TimeProvider clock, TimerWorker worker, CancellationToken ct) =>
{
    var delay = Task.Delay(TimeSpan.FromHours(1), clock, ct);
    worker.Waiting = true;
    await delay;
    return Results.Ok(new { now = clock.GetUtcNow() });
});
app.Run();

public sealed class ClockAppMarker;
public sealed class TimerWorker(TimeProvider clock) : IHostedService, IDisposable
{
    private ITimer? _timer;
    private int _ticks;
    public int Ticks => Volatile.Read(ref _ticks);
    public bool Waiting { get; set; }
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _timer = clock.CreateTimer(_ => Interlocked.Increment(ref _ticks), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
        return Task.CompletedTask;
    }
    public Task StopAsync(CancellationToken cancellationToken) { _timer?.Dispose(); return Task.CompletedTask; }
    public void Dispose() => _timer?.Dispose();
}
