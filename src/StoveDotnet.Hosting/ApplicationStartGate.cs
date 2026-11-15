namespace StoveDotnet.Hosting;

/// <summary>
/// Lets one application host build and start at a time in this process. Host building is observed through
/// process-wide diagnostics (WebApplicationFactory finds the host its entry point builds that way), so two
/// environments that start applications at the same moment can be handed each other's host. Dependencies still start
/// in parallel; only the short build-and-start of each application takes turns.
/// </summary>
public static class ApplicationStartGate
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    /// <summary>For application adapters: runs <paramref name="start"/> while no other application is starting.</summary>
    public static async Task<T> RunAsync<T>(Func<Task<T>> start, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(start);
        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await start().ConfigureAwait(false);
        }
        finally
        {
            Gate.Release();
        }
    }
}
