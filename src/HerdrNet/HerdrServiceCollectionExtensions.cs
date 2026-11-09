using HerdrNet;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Registers HerdrNet in a service collection.</summary>
public static class HerdrServiceCollectionExtensions
{
    /// <summary>
    /// Adds a singleton <see cref="ConnectionSupervisor"/> configured from <see cref="HerdrOptions"/>, started
    /// and stopped with the host. Stopping the host disposes the connection (CLOSE on every stream) and never
    /// stops the core. Without a host, call <see cref="ConnectionSupervisor.Start"/> yourself.
    /// </summary>
    public static IServiceCollection AddHerdr(this IServiceCollection services, Action<HerdrOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddOptions<HerdrOptions>();
        if (configure is not null)
            services.Configure(configure);
        services.TryAddSingleton(sp => new ConnectionSupervisor(
            sp.GetRequiredService<IOptions<HerdrOptions>>().Value,
            sp.GetService<ILoggerFactory>()));
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, HerdrHostedService>());
        return services;
    }

    sealed class HerdrHostedService(ConnectionSupervisor supervisor) : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken)
        {
            supervisor.Start();
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken) => supervisor.DisposeAsync().AsTask().WaitAsync(cancellationToken);
    }
}
