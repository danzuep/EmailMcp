using EmailMcp;
using MailKitSimplified.Receiver;
using MailKitSimplified.Sender;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Events;

var builder = Host.CreateApplicationBuilder(args);

// Configure Serilog file logger (keep stdio available for MCP)
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Debug()
    .MinimumLevel.Override("Microsoft", LogEventLevel.Information)
    .WriteTo.File("logs/email-mcp-.log", rollingInterval: RollingInterval.Day, retainedFileCountLimit: 7)
    .CreateLogger();

// Keep stdout clean for MCP stdio transport. Only errors go to stderr console.
builder.Logging.ClearProviders();
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Error);
builder.Logging.AddSerilog(Log.Logger, dispose: true);

var appsettingsPath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
builder.Configuration.AddJsonFile(appsettingsPath);

builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

// Register EmailService which resolves receiver/sender options and factories
builder.Services.AddMailKitSimplifiedEmailSender(builder.Configuration);
builder.Services.AddMailKitSimplifiedEmailReceiver(builder.Configuration);
builder.Services.AddSingleton<EmailService>();

var host = builder.Build();

// expose EmailService and typed logger to EmailTools so it can resolve injected receivers/senders/options
EmailTools.EmailService = host.Services.GetRequiredService<EmailService>();
var loggerFactory = host.Services.GetRequiredService<ILoggerFactory>();
EmailTools.Logger = loggerFactory.CreateLogger("EmailTools");

try
{
    await host.RunAsync();
}
catch (Exception ex)
{
    Log.Error("Runtime error: {Message}", ex.Message);
}
Log.CloseAndFlush();
