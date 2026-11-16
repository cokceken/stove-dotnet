namespace StoveDotnet;

/// <summary>Thrown by Stove's built-in assertions (e.g. "should have been called").</summary>
public sealed class StoveAssertionException : Exception
{
    /// <summary>Creates the exception with the assertion message.</summary>
    public StoveAssertionException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception, keeping the underlying cause.</summary>
    public StoveAssertionException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
