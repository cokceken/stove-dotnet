using Microsoft.Extensions.DependencyInjection;

namespace StoveDotnet.Hosting;

/// <summary>Access to the application's own services, resolved from a fresh DI scope.</summary>
public static class BridgeExtensions
{
    /// <summary>
    /// The system registered on the running environment: the instance with the given name; without a name, the
    /// unnamed instance or the only one registered.
    /// </summary>
    public static IServiceProvider Services(this StoveTestContext test, string name) => test.GetApplication(name).Services;

    /// <summary>
    /// Resolves <typeparamref name="T"/> from a fresh scope of the named application's own services and runs
    /// <paramref name="action"/> with it, for example to read state through a repository. The scope is disposed
    /// afterwards.
    /// </summary>
    public static async Task Using<T>(this StoveTestContext test, string application, Func<T, Task> action) where T : notnull
    {
        await using var scope = test.GetApplication(application).Services.CreateAsyncScope();
        await action(scope.ServiceProvider.GetRequiredService<T>()).ConfigureAwait(false);
    }

    /// <summary>
    /// Resolves <typeparamref name="T"/> from a fresh scope of the named application's own services and returns
    /// what <paramref name="action"/> produces, for example to read state through a repository. The scope is
    /// disposed afterwards.
    /// </summary>
    public static async Task<TResult> Using<T, TResult>(this StoveTestContext test, string application, Func<T, Task<TResult>> action) where T : notnull
    {
        await using var scope = test.GetApplication(application).Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<T>()).ConfigureAwait(false);
    }
    /// <summary>
    /// The system registered on the running environment: the instance with the given name; without a name, the
    /// unnamed instance or the only one registered.
    /// </summary>
    public static IServiceProvider Services(this StoveTestContext test) => test.Application.Services;

    /// <summary>
    /// Resolves <typeparamref name="T"/> from a fresh scope of the application's own services and runs <paramref
    /// name="action"/> with it, for example to read state through a repository. The scope is disposed afterwards.
    /// </summary>
    public static async Task Using<T>(this StoveTestContext test, Func<T, Task> action) where T : notnull
    {
        await using var scope = test.Application.Services.CreateAsyncScope();
        await action(scope.ServiceProvider.GetRequiredService<T>()).ConfigureAwait(false);
    }

    /// <summary>
    /// Resolves <typeparamref name="T"/> from a fresh scope of the application's own services and returns what
    /// <paramref name="action"/> produces, for example to read state through a repository. The scope is disposed
    /// afterwards.
    /// </summary>
    public static async Task<TResult> Using<T, TResult>(this StoveTestContext test, Func<T, Task<TResult>> action) where T : notnull
    {
        await using var scope = test.Application.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<T>()).ConfigureAwait(false);
    }

    /// <summary>
    /// Resolves two services from one fresh scope of the application's own services and runs <paramref
    /// name="action"/> with them.
    /// </summary>
    public static async Task Using<T1, T2>(this StoveTestContext test, Func<T1, T2, Task> action)
        where T1 : notnull
        where T2 : notnull
    {
        await using var scope = test.Application.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        await action(services.GetRequiredService<T1>(), services.GetRequiredService<T2>()).ConfigureAwait(false);
    }

    /// <summary>
    /// Resolves three services from one fresh scope of the application's own services and runs <paramref
    /// name="action"/> with them.
    /// </summary>
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
