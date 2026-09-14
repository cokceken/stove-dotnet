namespace StoveDotnet;

internal static class SystemDisposal
{
    public static async ValueTask RunAsync(IEnumerable<Func<ValueTask>> actions)
    {
        var errors = new List<Exception>();
        foreach (var action in actions)
        {
            try
            {
                await action().ConfigureAwait(false);
            }
            catch (Exception error)
            {
                errors.Add(error);
            }
        }

        if (errors.Count > 0)
        {
            throw new AggregateException("One or more Stove resources failed to stop.", errors);
        }
    }
}
