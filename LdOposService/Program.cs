using ApiLibrary;
using LdOposService;
using Serilog;
using Serilog.Events;



// Configure Serilog before building the host
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
    .MinimumLevel.Override("System", LogEventLevel.Warning)
    .Enrich.FromLogContext()
    .WriteTo.Console()
    .WriteTo.File(
        path: Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "LdOposService",
            "logs",
            "service-.log"
        ),
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 30,
        outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}"
    )
    .CreateLogger();

try
{
    Log.Information("Starting LdOposService Windows Service");

    var builder = Host.CreateDefaultBuilder(args)
        .UseSerilog()
        .ConfigureServices(services =>
        {
            services.AddSingleton<ApiServices>();
            services.AddHostedService<ScannedCodeService>();
        })
        .UseWindowsService(options =>
        {
            options.ServiceName = "LdOposService";
        });

    var host = builder.Build();
    await host.RunAsync();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Application terminated unexpectedly");
}
finally
{
    Log.CloseAndFlush();
}
