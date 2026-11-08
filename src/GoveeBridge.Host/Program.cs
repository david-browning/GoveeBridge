using GoveeBridge.Core;

namespace GoveeBridge.Host;

/// <summary>Builds the single-lamp background Host and binds its configuration.</summary>
internal static class Program
{
   public static async Task Main(string[] args)
   {
      var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder(args);

      // Windows Services may start in System32. Always resolve local JSON
      // configuration relative to the installed executable, never the CWD.
      builder.Configuration.AddJsonFile(
         Path.Combine(AppContext.BaseDirectory, "appsettings.json"),
         optional: false, reloadOnChange: false);
      builder.Configuration.AddJsonFile(
         Path.Combine(AppContext.BaseDirectory, "appsettings.local.json"),
         optional: true, reloadOnChange: false);

      builder.Services.AddWindowsService(options => options.ServiceName = "GoveeBridge");

      // Bind directly into the models that own the defaults and validation.
      // Invalid numbers and unknown transport names now fail at startup rather
      // than silently falling back to defaults.
      BridgeSettings bridgeSettings = new();
      builder.Configuration.GetSection("Bridge").Bind(bridgeSettings);
      bridgeSettings.Validate();

      BridgeHostOptions hostOptions = new();
      builder.Configuration.GetSection("Host").Bind(hostOptions);
      hostOptions.Validate();

      builder.Services.AddSingleton(bridgeSettings);
      builder.Services.AddSingleton(hostOptions);
      builder.Services.AddSingleton<IBridgeEventPublisher, NullBridgeEventPublisher>();
      builder.Services.AddSingleton<BridgeRuntime>();
      builder.Services.AddHostedService<BridgeWorker>();

      using IHost host = builder.Build();
      await host.RunAsync();
   }
}
