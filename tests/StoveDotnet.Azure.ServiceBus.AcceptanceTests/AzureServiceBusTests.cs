using System.Net;
using Azure.Core;
using Azure.Messaging.ServiceBus;
using StoveDotnet.AspNetCore;
using StoveDotnet.Http;
using Xunit;

namespace StoveDotnet.Azure.ServiceBus.AcceptanceTests;

public sealed class AzureServiceBusConfigurationTests
{
    [Fact]
    public async Task Managed_emulator_requires_explicit_license_acceptance()
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            StoveBuilder.Create().WithAzureServiceBus().StartAsync(TestContext.Current.CancellationToken));
        Assert.Contains("AcceptLicenseAgreement", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Existing_namespace_accepts_a_caller_owned_token_credential()
    {
        var credential = new StubTokenCredential();
        await using var stove = await StoveBuilder.Create().WithAzureServiceBus(o =>
            o.UseExisting("orders.servicebus.windows.net", credential, runTopologySetup: false))
            .StartAsync(TestContext.Current.CancellationToken);
        await stove.Test(t =>
        {
            Assert.Equal("orders.servicebus.windows.net", t.AzureServiceBus().ExposedConfiguration.FullyQualifiedNamespace);
            Assert.Null(t.AzureServiceBus().ExposedConfiguration.ConnectionString);
            return Task.CompletedTask;
        });
    }

    private sealed class StubTokenCredential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            new("unused", DateTimeOffset.UtcNow.AddMinutes(5));
        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            ValueTask.FromResult(GetToken(requestContext, cancellationToken));
    }
}

public sealed class AzureServiceBusFixture : IAsyncLifetime
{
    public Stove Stove { get; private set; } = null!;
    public AzureServiceBusSystem System { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        Stove = await StoveBuilder.Create()
            .WithAzureServiceBus(o =>
            {
                o.AcceptLicenseAgreement = true;
                o.Topology
                    .Queue("inbox")
                    .Queue("test-output")
                    .Queue("scheduled")
                    .Topic("events", subscriptions: t => t.Subscription("tests"));
                o.ConfigureExposedConfiguration = c => [new("ServiceBus:ConnectionString", c.ConnectionString)];
            })
            .WithHttpClient()
            .WithAspNetCoreApplication<Program>()
            .StartAsync(TestContext.Current.CancellationToken);
        await Stove.Test(t => { System = t.AzureServiceBus(); return Task.CompletedTask; });
    }

    public async ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        if (Stove is not null) await Stove.DisposeAsync();
    }
}

public sealed class AzureServiceBusTests(AzureServiceBusFixture fixture) : IClassFixture<AzureServiceBusFixture>
{
    [Fact]
    public Task Stove_publication_drives_the_real_application_consumer() => fixture.Stove.Test(async t =>
    {
        var work = new Work(Guid.NewGuid().ToString("N"), "processed");
        await t.AzureServiceBus().Queue("inbox").Publish(work);
        await Eventually.AssertAsync(async ct =>
        {
            var response = await t.Http().Get<Work>($"/processed/{work.Id}");
            if (response.StatusCode != HttpStatusCode.OK || response.Body.Value != work.Value)
                throw new StoveAssertionException("The Service Bus consumer has not committed its business effect yet.");
        }, TimeSpan.FromSeconds(10), cancellationToken: t.CancellationToken);
    });

    [Fact]
    public Task Queue_messages_can_be_received_and_explicitly_completed() => fixture.Stove.Test(async t =>
    {
        var expected = new Work(Guid.NewGuid().ToString("N"), "queue");
        await t.AzureServiceBus().Queue("test-output").Publish(expected);
        await using var delivery = await t.AzureServiceBus().Queue("test-output").Receive<Work>();
        Assert.Equal(expected, delivery.Value);
        Assert.Equal(t.TestId, delivery.NativeMessage.ApplicationProperties[StoveHeaders.TestId]);
        await delivery.Complete();
    });

    [Fact]
    public Task Topic_messages_can_be_received_from_a_subscription() => fixture.Stove.Test(async t =>
    {
        var expected = new Work(Guid.NewGuid().ToString("N"), "topic");
        await t.AzureServiceBus().Topic("events").Publish(expected);
        await using var delivery = await t.AzureServiceBus().Subscription("events", "tests").Receive<Work>();
        Assert.Equal(expected, delivery.Value);
        await delivery.Complete();
    });

    [Fact]
    public Task Scheduled_messages_are_inspected_without_waiting_for_activation() => fixture.Stove.Test(async t =>
    {
        var expected = new Work(Guid.NewGuid().ToString("N"), "later");
        var scheduledAt = DateTimeOffset.UtcNow.AddDays(7);
        await t.AzureServiceBus().Queue("scheduled").Schedule(expected, scheduledAt);
        var actual = await t.AzureServiceBus().Queue("scheduled").ShouldBeScheduled<Work>(m => m.Value.Id == expected.Id);
        Assert.Equal(expected, actual.Value);
        Assert.InRange(actual.NativeMessage.ScheduledEnqueueTime, scheduledAt.AddSeconds(-1), scheduledAt.AddSeconds(1));
        Assert.Equal(ServiceBusMessageState.Scheduled, actual.NativeMessage.State);
    });

    [Fact]
    public async Task Scope_cleanup_cancels_long_running_scheduled_messages()
    {
        long sequenceNumber = 0;
        await fixture.Stove.Test(async t =>
        {
            sequenceNumber = await t.AzureServiceBus().Queue("scheduled")
                .Schedule(new Work(Guid.NewGuid().ToString("N"), "cleanup"), DateTimeOffset.UtcNow.AddDays(7));
            await t.AzureServiceBus().Queue("scheduled").ShouldBeScheduled<Work>(m => m.NativeMessage.SequenceNumber == sequenceNumber);
        });

        await using var receiver = fixture.System.Client.CreateReceiver("scheduled");
        var remaining = await receiver.PeekMessagesAsync(1_000, 0, TestContext.Current.CancellationToken);
        Assert.DoesNotContain(remaining, message => message.SequenceNumber == sequenceNumber);
    }

    [Fact]
    public async Task Overlapping_scopes_peek_only_their_correlated_scheduled_messages()
    {
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var count = 0;
        await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => fixture.Stove.Test(async t =>
        {
            if (Interlocked.Increment(ref count) == 3) ready.SetResult();
            await ready.Task.WaitAsync(t.CancellationToken);
            await t.AzureServiceBus().Queue("scheduled").Schedule(new Work(t.TestId, "parallel"), DateTimeOffset.UtcNow.AddDays(7));
            var messages = await t.AzureServiceBus().Queue("scheduled").Peek<Work>();
            Assert.Single(messages);
            Assert.Equal(t.TestId, messages[0].Value.Id);
        })));
    }

    [Fact]
    public async Task Existing_endpoint_keeps_the_external_emulator_running()
    {
        await using (var stove = await StoveBuilder.Create().WithAzureServiceBus(o =>
            o.UseExisting(fixture.System.ExposedConfiguration.ConnectionString!, runTopologySetup: false))
            .StartAsync(TestContext.Current.CancellationToken))
        {
            await stove.Test(async t =>
            {
                var expected = new Work(Guid.NewGuid().ToString("N"), "existing");
                await t.AzureServiceBus().Queue("test-output").Publish(expected);
                await using var delivery = await t.AzureServiceBus().Queue("test-output").Receive<Work>();
                Assert.Equal(expected, delivery.Value);
                await delivery.Complete();
            });
        }
        Assert.False(fixture.System.Client.IsClosed);
    }
}
