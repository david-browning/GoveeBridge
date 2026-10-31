using GoveeBridge.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace GoveeBridge.Host;

internal static class Program
{
    public static async Task Main(string[] args)
    {
        HostApplicationBuilder builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder(args);
        // Windows Services may start from System32; use the executable directory.
        builder.Configuration.AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.json"),
            optional: false, reloadOnChange: false);
        builder.Configuration.AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.local.json"),
            optional: true, reloadOnChange: false);
        builder.Services.AddWindowsService(options => options.ServiceName = "GoveeBridge");

        BridgeSettings settings = LoadSettings(builder.Configuration);
        BridgeHostOptions hostOptions = LoadHostOptions(builder.Configuration);
        settings.Validate();
        hostOptions.Validate();

        builder.Services.AddSingleton(settings);
        builder.Services.AddSingleton(hostOptions);
        builder.Services.AddSingleton<IBridgeEventPublisher, NullBridgeEventPublisher>();
        builder.Services.AddSingleton<BridgeRuntime>();
        builder.Services.AddHostedService<BridgeWorker>();

        using IHost host = builder.Build();
        await host.RunAsync();
    }

    private static BridgeSettings LoadSettings(IConfiguration config)
    {
        var defaults = new BridgeSettings();
        string? mode = config["Bridge:TransportMode"];
        if (!Enum.TryParse(mode, ignoreCase: true, out TransportMode parsedMode))
            throw new ArgumentException($"Unknown Bridge:TransportMode: {mode}");
        return new BridgeSettings
        {
            TransportMode = parsedMode,
            LampIp = config["Bridge:LampIp"],
            FrameListenPort = ReadInt(config, "Bridge:FrameListenPort", defaults.FrameListenPort),
            LogicalLedCount = ReadInt(config, "Bridge:LogicalLedCount", defaults.LogicalLedCount),
            MinimumSendIntervalMs = ReadInt(config, "Bridge:MinimumSendIntervalMs", defaults.MinimumSendIntervalMs),
            RemotePollIntervalMs = ReadInt(config, "Bridge:RemotePollIntervalMs", defaults.RemotePollIntervalMs),
            LampTimeoutMs = ReadInt(config, "Bridge:LampTimeoutMs", defaults.LampTimeoutMs),
            AutoReleaseWhenRemoteTurnsOff = ReadBool(config,
                "Bridge:AutoReleaseWhenRemoteTurnsOff", defaults.AutoReleaseWhenRemoteTurnsOff)
        };
    }

    private static BridgeHostOptions LoadHostOptions(IConfiguration config)
    {
        var defaults = new BridgeHostOptions();
        return new BridgeHostOptions
        {
            EnableLampMonitoring = ReadBool(config, "Host:EnableLampMonitoring", defaults.EnableLampMonitoring),
            LogLampStatusChanges = ReadBool(config, "Host:LogLampStatusChanges", defaults.LogLampStatusChanges),
            FramePumpIntervalMs = ReadInt(config, "Host:FramePumpIntervalMs", defaults.FramePumpIntervalMs),
            FrameStaleAfterMs = ReadInt(config, "Host:FrameStaleAfterMs", defaults.FrameStaleAfterMs),
            AutoReleaseOnColorMismatch = ReadBool(config,
                "Host:AutoReleaseOnColorMismatch", defaults.AutoReleaseOnColorMismatch),
            ColorMismatchGraceMs = ReadInt(config,
                "Host:ColorMismatchGraceMs", defaults.ColorMismatchGraceMs),
            ColorMismatchRequiredPolls = ReadInt(config,
                "Host:ColorMismatchRequiredPolls", defaults.ColorMismatchRequiredPolls)
        };
    }

    private static int ReadInt(IConfiguration config, string path, int fallback) =>
        int.TryParse(config[path], out int value) ? value : fallback;

    private static bool ReadBool(IConfiguration config, string path, bool fallback) =>
        bool.TryParse(config[path], out bool value) ? value : fallback;
}
