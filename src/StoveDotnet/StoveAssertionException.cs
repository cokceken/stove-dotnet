namespace StoveDotnet;

/// <summary>Thrown by Stove's built-in assertions (e.g. "should have been called").</summary>
public sealed class StoveAssertionException : Exception
{
    public StoveAssertionException(string message)
        : base(message)
    {
    }

    public StoveAssertionException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
