using Microsoft.Extensions.DependencyInjection;

namespace VelaShell.Controls.DependencyInjection;

public static class ControlsServiceCollectionExtensions
{
    public static IServiceCollection AddVelaShellControls(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        return services;
    }
}
