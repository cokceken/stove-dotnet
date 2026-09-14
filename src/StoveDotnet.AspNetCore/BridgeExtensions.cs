using Microsoft.Extensions.DependencyInjection;

namespace StoveDotnet.AspNetCore;

/// <summary>Access to the application's own services, resolved from a fresh DI scope.</summary>
public static class BridgeExtensions
{
    public static IServiceProvider Services(this StoveTestContext test) => test.Application.Services;

    public static async Task Using<T>(this StoveTestContext test, Func<T, Task> action) where T : notnull
    {
        await using var scope = test.Application.Services.CreateAsyncScope();
        await action(scope.ServiceProvider.GetRequiredService<T>()).ConfigureAwait(false);
    }

    public static async Task<TResult> Using<T, TResult>(this StoveTestContext test, Func<T, Task<TResult>> action) where T : notnull
    {
        await using var scope = test.Application.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<T>()).ConfigureAwait(false);
    }

    public static async Task Using<T1, T2>(this StoveTestContext test, Func<T1, T2, Task> action)
        where T1 : notnull
        where T2 : notnull
    {
        await using var scope = test.Application.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        await action(services.GetRequiredService<T1>(), services.GetRequiredService<T2>()).ConfigureAwait(false);
    }

    public static async Task Using<T1, T2, T3>(this StoveTestContext test, Func<T1, T2, T3, Task> action)
        where T1 : notnull
        where T2 : notnull
        where T3 : notnull
    {
        await using var scope = test.Application.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        await action(services.GetRequiredService<T1>(), services.GetRequiredService<T2>(), services.GetRequiredService<T3>())
            .ConfigureAwait(false);
    }
}
