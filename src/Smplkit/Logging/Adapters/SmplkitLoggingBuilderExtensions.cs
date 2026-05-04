using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Smplkit.Logging.Adapters;

/// <summary>
/// <see cref="ILoggingBuilder"/> extensions for wiring a smplkit
/// <see cref="MicrosoftLoggingAdapter"/> into the host's logging pipeline.
/// </summary>
public static class SmplkitLoggingBuilderExtensions
{
    /// <summary>
    /// Registers a <see cref="MicrosoftLoggingAdapter"/> as the host's
    /// <see cref="ILoggerProvider"/>, <see cref="IConfigureOptions{LoggerFilterOptions}"/>,
    /// and <see cref="IOptionsChangeTokenSource{LoggerFilterOptions}"/>, and
    /// attaches it to <paramref name="client"/>'s logging plane.
    /// </summary>
    /// <remarks>
    /// <para>After this call, every <c>ILoggerFactory.CreateLogger(name)</c> in
    /// the host is observed by smplkit (auto-discovery), and any server-managed
    /// level pushed by <c>client.Logging.InstallAsync()</c> or the WebSocket
    /// triggers a host-level filter refresh that gates output to <em>every</em>
    /// other registered provider.</para>
    /// <para>Call <c>await client.Logging.InstallAsync()</c> at startup to
    /// bulk-register discovered categories with the server and apply current
    /// managed levels.</para>
    /// </remarks>
    /// <param name="builder">The logging builder.</param>
    /// <param name="client">The smplkit runtime client whose logging plane will manage levels.</param>
    /// <returns>The same <paramref name="builder"/> for chaining.</returns>
    public static ILoggingBuilder AddSmplkit(this ILoggingBuilder builder, SmplClient client)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(client);

        var adapter = new MicrosoftLoggingAdapter();
        builder.Services.AddSingleton<ILoggerProvider>(adapter);
        builder.Services.AddSingleton<IConfigureOptions<LoggerFilterOptions>>(adapter);
        builder.Services.AddSingleton<IOptionsChangeTokenSource<LoggerFilterOptions>>(adapter);
        client.Logging.RegisterAdapter(adapter);
        return builder;
    }
}
