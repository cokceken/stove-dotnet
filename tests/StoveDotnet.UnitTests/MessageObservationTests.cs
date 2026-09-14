using Xunit;

namespace StoveDotnet.UnitTests;

public sealed class MessageObservationTests
{
    [Fact]
    public async Task Strict_correlation_rejects_missing_malformed_and_conflicting_headers()
    {
        await using var stove = await StoveBuilder.Create().StartAsync();
        var buffer = new ScopedMessageBuffer<string>(new());
        await stove.Test(t =>
        {
            buffer.Start(t);
            buffer.Add("missing", 1, null, null);
            buffer.Add("malformed", 1, t.TestId, "invalid");
            buffer.Add("conflict", 1, t.TestId, TraceContext.FormatTraceparent(TraceContext.NewTraceId(), TraceContext.NewSpanId()));
            buffer.Add("foreign", 1, "other", t.Traceparent);
            buffer.Add("trace", 1, null, t.Traceparent);
            buffer.Add("test", 1, t.TestId, null);
            buffer.Add("both", 1, t.TestId, t.Traceparent);
            Assert.Equal(["trace", "test", "both"], buffer.Snapshot(t));
            buffer.End(t, null);
            Assert.Empty(buffer.Inspect(t).Records);
            return Task.CompletedTask;
        });
    }

    [Theory]
    [InlineData(1, 100)]
    [InlineData(100, 1)]
    public async Task Limits_fail_explicitly_and_keep_only_bounded_diagnostic_evidence(int count, long bytes)
    {
        await using var stove = await StoveBuilder.Create().StartAsync();
        var buffer = new ScopedMessageBuffer<string>(new() { MaxMessagesPerTest = count, MaxBytesPerTest = bytes });
        await stove.Test(t =>
        {
            buffer.Start(t);
            buffer.Add("first", 1, t.TestId, null);
            buffer.Add("overflow", 1, t.TestId, null);
            for (var i = 0; i < 100; i++) buffer.Add("later", 1000, t.TestId, null);
            Assert.Throws<StoveAssertionException>(() => buffer.Snapshot(t));
            buffer.End(t, new InvalidOperationException("failed assertion"));
            Assert.Equal(["first"], buffer.Inspect(t).Records);
            Assert.Contains("retention limit", buffer.Inspect(t).Error!, StringComparison.Ordinal);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Oversized_single_record_is_not_retained()
    {
        await using var stove = await StoveBuilder.Create().StartAsync();
        var buffer = new ScopedMessageBuffer<string>(new() { MaxBytesPerTest = 1 });
        await stove.Test(t =>
        {
            buffer.Start(t);
            buffer.Add("large", 2, t.TestId, null);
            Assert.Empty(buffer.Inspect(t).Records);
            Assert.Throws<StoveAssertionException>(() => buffer.Snapshot(t));
            buffer.End(t, null);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Fallback_uses_only_the_single_active_scope_and_never_replays_old_records()
    {
        await using var stove = await StoveBuilder.Create().StartAsync();
        var buffer = new ScopedMessageBuffer<string>(new() { UncorrelatedMessages = UncorrelatedMessagePolicy.SingleActiveTest });
        buffer.Add("before", 1, null, null);
        await stove.Test(t =>
        {
            buffer.Start(t);
            buffer.Add("current", 1, null, null);
            buffer.Add("malformed", 1, null, "bad");
            Assert.Equal(["current"], buffer.Snapshot(t));
            buffer.End(t, null);
            return Task.CompletedTask;
        });
        buffer.Add("between", 1, null, null);
        var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var count = 0;
        await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => stove.Test(async t =>
        {
            buffer.Start(t);
            if (Interlocked.Increment(ref count) == 2)
            {
                buffer.Add("ambiguous", 1, null, null);
                barrier.SetResult();
            }
            await barrier.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Empty(buffer.Snapshot(t));
            buffer.Add(t.TestId, 1, t.TestId, t.Traceparent);
            Assert.Equal([t.TestId], buffer.Snapshot(t));
            buffer.End(t, null);
        })));
    }

    [Fact]
    public async Task Failed_scope_evidence_is_not_mutated_by_late_deliveries()
    {
        await using var stove = await StoveBuilder.Create().StartAsync();
        var buffer = new ScopedMessageBuffer<string>(new());
        await stove.Test(t =>
        {
            buffer.Start(t);
            buffer.Add("before", 1, t.TestId, null);
            buffer.End(t, new InvalidOperationException());
            buffer.Add("after", 1, t.TestId, null);
            Assert.Equal(["before"], buffer.Inspect(t).Records);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Observer_failure_invalidates_current_and_future_assertions()
    {
        await using var stove = await StoveBuilder.Create().StartAsync();
        var buffer = new ScopedMessageBuffer<string>(new());
        buffer.Fail("connection lost");
        await stove.Test(t =>
        {
            buffer.Start(t);
            Assert.Contains("connection lost", Assert.Throws<StoveAssertionException>(() => buffer.Snapshot(t)).Message, StringComparison.Ordinal);
            buffer.End(t, null);
            return Task.CompletedTask;
        });
    }
}
