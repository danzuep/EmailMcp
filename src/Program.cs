using EmailMcp;
using MailKitSimplified.Receiver;
using MailKitSimplified.Receiver.Abstractions;
using MailKitSimplified.Receiver.Models;
using MailKitSimplified.Receiver.Services;
using MailKitSimplified.Sender;
using MailKitSimplified.Sender.Abstractions;
using MailKitSimplified.Sender.Models;
using MailKitSimplified.Sender.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Serilog;
using Serilog.Events;

var builder = Host.CreateApplicationBuilder(args);

Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory, "logs"));

// Configure Serilog file logger (keep stdio available for MCP)
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Debug()
    .MinimumLevel.Override("Microsoft", LogEventLevel.Information)
    .WriteTo.File(Path.Combine(AppContext.BaseDirectory, "logs", "email-mcp-.log"), rollingInterval: RollingInterval.Day, retainedFileCountLimit: 7)
    .WriteTo.Debug()
    .CreateLogger();

// Keep stdout clean for MCP stdio transport. Only errors go to stderr console.
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Error);
builder.Logging.AddFilter("ModelContextProtocol", LogLevel.Error);
builder.Logging.AddFilter("ModelContextProtocol.Server.StdioServerTransport", LogLevel.Error);
builder.Logging.AddFilter("Microsoft", LogLevel.Warning);
builder.Logging.AddSerilog(Log.Logger, dispose: true);

var appsettingsPath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
builder.Configuration.AddJsonFile(appsettingsPath, optional: false, reloadOnChange: false);

var senderSection = builder.Configuration.GetSection("EmailSender");
var receiverSection = builder.Configuration.GetSection("EmailReceiver");
if (!senderSection.Exists() || !receiverSection.Exists())
{
    throw new InvalidOperationException(
        "Missing required EmailSender and EmailReceiver configuration sections. Ensure appsettings.json is copied to the output directory and contains both sections.");
}

builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

// Register EmailService which resolves receiver/sender options and factories
builder.Services.AddMailKitSimplifiedEmailSender(builder.Configuration);
builder.Services.AddMailKitSimplifiedEmailReceiver(builder.Configuration);
builder.Services.AddSingleton<ImapReceiver>(p => (ImapReceiver)p.GetRequiredService<IImapReceiver>());
builder.Services.AddSingleton<SmtpSender>(p => (SmtpSender)p.GetRequiredService<ISmtpSender>());
builder.Services.AddSingleton<EmailService>();

var host = builder.Build();

try
{
    // _ = host.Services.GetRequiredService<EmailService>();
    await host.RunAsync();
}
catch (Exception ex)
{
    Log.Error("Runtime error: {Message}", ex.Message);
}
Log.CloseAndFlush();
