using StoveDotnet;
using StoveDotnet.WireMock;

namespace OrderService.E2ETests.Fakes;

/// <summary>
/// Fake of the Inventory API, generated from <c>examples/OrderService/specs/inventory-api.yaml</c>.
/// Covers only the operations OrderService calls: <c>getStock</c>.
/// Note: OrderService reads stock with <c>GetFromJsonAsync</c>, so <see cref="ProductUnknown"/> makes it respond 500.
/// </summary>
public sealed class InventoryFake(WireMockSystem wireMock)
{
    public const string InstanceName = "inventory";

    /// <summary>The underlying WireMock instance, for scenarios this fake does not cover.</summary>
    public WireMockSystem WireMock { get; } = wireMock;

    // getStock: GET /stock/{productId}

    /// <summary>200: the product has <paramref name="available"/> units in stock.</summary>
    public InventoryFake StockAvailable(string productId, int available)
    {
        WireMock.MockGet($"/stock/{productId}", statusCode: 200, responseBody: new InventoryStock(available));
        return this;
    }

    /// <summary>404: the product does not exist.</summary>
    public InventoryFake ProductUnknown(string productId)
    {
        WireMock.MockGet($"/stock/{productId}", statusCode: 404, responseBody: new InventoryError($"product {productId} not found"));
        return this;
    }

    /// <summary>Asserts the stock of <paramref name="productId"/> was checked <paramref name="times"/> time(s).</summary>
    public async Task ShouldHaveCheckedStock(string productId, int times = 1, TimeSpan? within = null) =>
        await WireMock.ShouldHaveBeenCalled("GET", $"/stock/{productId}", times, within);

    /// <summary>Asserts no stock was checked for any product.</summary>
    public async Task ShouldNotHaveCheckedStock() =>
        await WireMock.ShouldNotHaveBeenCalled("GET", "/stock/{productId}");
}

// Wire models from components/schemas. Kept separate from the application's own types so the tests check the contract.
public sealed record InventoryStock(int Available);

public sealed record InventoryError(string Error);

public static class InventoryFakeExtensions
{
    /// <summary>Registers the fake and points the application's Inventory client at it.</summary>
    public static StoveBuilder WithInventoryFake(this StoveBuilder builder, string baseUrlKey = "Inventory:BaseUrl") =>
        builder.WithWireMock(Fakes.InventoryFake.InstanceName, o =>
            o.ConfigureExposedConfiguration = c => [new(baseUrlKey, c.BaseUrl.ToString())]);

    public static InventoryFake InventoryFake(this StoveTestContext test) =>
        new(test.WireMock(Fakes.InventoryFake.InstanceName));
}
