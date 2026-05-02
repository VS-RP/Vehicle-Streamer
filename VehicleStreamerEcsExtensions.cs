using System;
using Microsoft.Extensions.DependencyInjection;

namespace SampSharp.VehicleStreamer.Entities;

/// <summary>
/// Container helpers for wiring <see cref="IVehicleStreamerService"/> into a
/// SampSharp ECS host.
/// </summary>
public static class VehicleStreamerEcsExtensions
{
    extension(IServiceCollection services)
    {
        /// <summary>
        /// Register <see cref="IVehicleStreamerService"/> with default options.
        /// </summary>
        /// <remarks>
        /// The host is responsible for calling <see cref="IVehicleStreamerService.Tick"/>
        /// on a periodic schedule (1–5 s typical). The library deliberately does not
        /// start its own timer — the cadence and main-thread synchronisation belong
        /// to the gamemode's existing tick loop.
        /// </remarks>
        public IServiceCollection AddVehicleStreamer()
            =>
                services.AddVehicleStreamer(_ => { });

        /// <summary>
        /// Register <see cref="IVehicleStreamerService"/> with caller-tuned options.
        /// </summary>
        public IServiceCollection AddVehicleStreamer(Action<VehicleStreamerOptions> configure)
        {
            ArgumentNullException.ThrowIfNull(services);
            ArgumentNullException.ThrowIfNull(configure);

            var options = new VehicleStreamerOptions();
            configure(options);

            services.AddSingleton(options);
            services.AddSingleton<IVehicleStreamerService, VehicleStreamerService>();
            return services;
        }
    }
}
