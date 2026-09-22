using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;

namespace StoveDotnet.Time;

/// <summary>Explicit clock injection. The fixture owns the clock; sharing an instance shares timestamps and timers.</summary>
public static class TestTimeExtensions
{
    /// <summary>Opt-in replacement of TimeProvider. Pass the same clock to hosts that should share time.</summary>
    public static IServiceCollection UseStoveTime(this IServiceCollection services, FakeTimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(clock);
        services.RemoveAll<TimeProvider>();
        services.RemoveAll<FakeTimeProvider>();
        services.AddSingleton<TimeProvider>(clock);
        services.AddSingleton(clock);
        return services;
    }

    public static FakeTimeProvider Clock(this StoveTestContext test, string? application = null) =>
        test.GetApplication(application).Services.GetRequiredService<FakeTimeProvider>();
}
