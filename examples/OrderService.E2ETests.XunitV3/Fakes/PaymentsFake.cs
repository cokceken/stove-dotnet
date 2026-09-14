using StoveDotnet;
using StoveDotnet.WireMock;

namespace OrderService.E2ETests.Fakes;

/// <summary>
/// Fake of the Payments API, generated from <c>examples/OrderService/specs/payments-api.yaml</c>.
/// Covers only the operations OrderService calls: <c>createCharge</c>.
/// </summary>
public sealed class PaymentsFake(WireMockSystem wireMock)
{
    public const string InstanceName = "payments";

    /// <summary>The underlying WireMock instance, for scenarios this fake does not cover.</summary>
    public WireMockSystem WireMock { get; } = wireMock;

    // createCharge: POST /charges

    /// <summary>200: the charge succeeds with <paramref name="paymentId"/>.</summary>
    public PaymentsFake ChargeSucceeds(string paymentId = "pay_8f2c1")
    {
        WireMock.MockPost("/charges", statusCode: 200, responseBody: new PaymentsCharge(paymentId));
        return this;
    }

    /// <summary>402: the charge is declined.</summary>
    public PaymentsFake ChargeDeclined(string code = "card_declined", string message = "The card was declined.")
    {
        WireMock.MockPost("/charges", statusCode: 402, responseBody: new PaymentsError(code, message));
        return this;
    }

    /// <summary>503: the provider is unavailable.</summary>
    public PaymentsFake PaymentsUnavailable()
    {
        WireMock.MockPost("/charges", statusCode: 503);
        return this;
    }

    /// <summary>
    /// Asserts exactly one charge was requested (and, when given, that it satisfies <paramref name="matching"/>) and
    /// returns the request body for further assertions.
    /// </summary>
    public async Task<PaymentsChargeRequest> ShouldHaveCharged(Func<PaymentsChargeRequest, bool>? matching = null, TimeSpan? within = null)
    {
        // ShouldHaveBeenCalled asserts the exact count (1 by default), so the first request is the one.
        var requests = await WireMock.ShouldHaveBeenCalled("POST", "/charges", within: within);
        var charge = requests[0].BodyAs<PaymentsChargeRequest>();
        if (matching is not null && !matching(charge))
        {
            throw new StoveAssertionException($"{InstanceName}: a charge was requested but it did not match the expectation: {charge}");
        }

        return charge;
    }

    public async Task ShouldNotHaveCharged() =>
        await WireMock.ShouldNotHaveBeenCalled("POST", "/charges");
}

// Wire models from components/schemas. Kept separate from the application's own types so the tests check the contract.
public sealed record PaymentsChargeRequest(Guid OrderId, string CustomerId, decimal Amount);

public sealed record PaymentsCharge(string PaymentId);

public sealed record PaymentsError(string Code, string Message);

public static class PaymentsFakeExtensions
{
    /// <summary>Registers the fake and points the application's Payments client at it.</summary>
    public static StoveBuilder WithPaymentsFake(this StoveBuilder builder, string baseUrlKey = "Payments:BaseUrl") =>
        builder.WithWireMock(Fakes.PaymentsFake.InstanceName, o =>
        {
            o.ScopeStubsToTest = true;
            o.ConfigureExposedConfiguration = c => [new(baseUrlKey, c.BaseUrl.ToString())];
        });

    public static PaymentsFake PaymentsFake(this StoveTestContext test) =>
        new(test.WireMock(Fakes.PaymentsFake.InstanceName));
}
