namespace StoveDotnet;

/// <summary>Configuration and readiness specific to one application.</summary>
public class ApplicationOptions
{
    /// <summary>Overrides shared dependency configuration for this application only.</summary>
    public IDictionary<string, string?> Configuration { get; } = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Optional readiness check after host startup, before the next application starts.</summary>
    public Func<IApplicationContext, CancellationToken, Task>? ReadyAsync { get; set; }
}

internal sealed record ApplicationRegistration(string? Name, IApplicationUnderTest Application, ApplicationOptions Options);

/// <summary>Systems that need the running applications, e.g. a base address or DI container. Runs after all applications are ready.</summary>
public interface IAfterApplicationsStarted
{
    /// <summary>Called once, after every application has started and passed its readiness check.</summary>
    Task OnApplicationsStartedAsync(Stove stove, CancellationToken cancellationToken);
}
