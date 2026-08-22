#:package Testcontainers@4.9.0
#:property PublishTrimmed=false
#:property JsonSerializerIsReflectionEnabledByDefault=true

using System.Collections;
using System.Diagnostics;
using System.Globalization;
using System.Net.Mail;
using System.Text;
using System.Text.Json;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Networks;

await using var smtp4Dev = new Smtp4DevFixture();
Console.WriteLine("[1/5] Starting smtp4dev with Testcontainers...");
await smtp4Dev.StartAsync();
var smtpPort = smtp4Dev.SmtpPort;
var imapPort = smtp4Dev.ImapPort;
Console.WriteLine($"smtp4dev is ready on localhost:{smtpPort} / {imapPort}");

Console.WriteLine("[2/5] Starting EmailMcp server...");
var env = Environment.GetEnvironmentVariables().Cast<DictionaryEntry>()
    .ToDictionary(kvp => kvp.Key!.ToString()!, kvp => kvp.Value?.ToString() ?? "");
env["EmailSender__SmtpHost"] = "localhost";
env["EmailSender__SmtpPort"] = smtpPort.ToString();
env["EmailSender__SmtpCredential__UserName"] = "";
env["EmailSender__SmtpCredential__Password"] = "";
env["EmailReceiver__ImapHost"] = "localhost";
env["EmailReceiver__ImapPort"] = imapPort.ToString();
env["EmailReceiver__ImapCredential__UserName"] = "";
env["EmailReceiver__ImapCredential__Password"] = "";
env["EmailReceiver__MailFolderName"] = "INBOX";

using var proc = new Process
{
    StartInfo = new ProcessStartInfo
    {
        FileName = "dotnet",
        Arguments = "run --no-launch-profile --project src/EmailMcp.csproj",
        WorkingDirectory = Directory.GetCurrentDirectory(),
        RedirectStandardInput = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        CreateNoWindow = true
    }
};

foreach (var kvp in env)
    proc.StartInfo.Environment[kvp.Key] = kvp.Value;

if (!proc.Start())
    throw new Exception("Failed to start EmailMcp process.");

var stderrTask = proc.StandardError.ReadToEndAsync();

string ReadFrame()
{
    // The MCP server outputs a single complete JSON object per line.
    var line = proc.StandardOutput.ReadLine();
    
    if (line is null)
    {
        var stderr = stderrTask.GetAwaiter().GetResult();
        throw new InvalidOperationException(
            $"EmailMcp exited with code {proc.ExitCode} before sending an MCP frame.\n{stderr}");
    }
    
    return line; 
}

void SendFrame(string json)
{
    // StandardInput is a StreamWriter, so we can just use WriteLine to append the required newline.
    proc.StandardInput.WriteLine(json);
    proc.StandardInput.Flush();
}

static string EscapeJson(string value) => value
    .Replace("\\", "\\\\")
    .Replace("\"", "\\\"")
    .Replace("\r", "\\r")
    .Replace("\n", "\\n")
    .Replace("\t", "\\t");

static string JsonString(string value) => "\"" + EscapeJson(value) + "\"";

static string BuildJsonValue(object? value)
{
    if (value is null) return "null";
    if (value is string s) return JsonString(s);
    if (value is bool b) return b ? "true" : "false";
    if (value is int or long or uint or ulong or short or ushort or byte or sbyte)
        return Convert.ToString(value, CultureInfo.InvariantCulture)!;
    if (value is double d) return d.ToString(CultureInfo.InvariantCulture);
    if (value is decimal dec) return dec.ToString(CultureInfo.InvariantCulture);
    if (value is DateTime dt) return JsonString(dt.ToString("O", CultureInfo.InvariantCulture));
    if (value is IDictionary dictionary)
    {
        var dictionaryProperties = new List<string>();
        foreach (DictionaryEntry entry in dictionary)
        {
            dictionaryProperties.Add(JsonString(entry.Key?.ToString() ?? "") + ":" + BuildJsonValue(entry.Value));
        }

        return "{" + string.Join(",", dictionaryProperties) + "}";
    }
    if (value is IEnumerable enumerable && value is not string)
    {
        var items = new List<string>();
        foreach (var item in enumerable)
            items.Add(BuildJsonValue(item));
        return "[" + string.Join(",", items) + "]";
    }

    var json = new StringBuilder();
    json.Append('{');
    var first = true;
    var type = value.GetType();
#pragma warning disable IL2075
    var properties = type.GetProperties();
#pragma warning restore IL2075
    foreach (var p in properties)
    {
        if (p.GetIndexParameters().Length > 0) continue;
        if (!first) json.Append(',');
        json.Append(JsonString(p.Name));
        json.Append(':');
        json.Append(BuildJsonValue(p.GetValue(value)));
        first = false;
    }
    json.Append('}');
    return json.ToString();
}

static string JsonRpc(int? id, string method, object? @params = null)
{
    var sb = new StringBuilder();
    sb.Append("{\"jsonrpc\":\"2.0\"");
    if (id is not null)
        sb.Append(",\"id\":").Append(id.Value);
    sb.Append(",\"method\":").Append(JsonString(method));
    if (@params is not null)
        sb.Append(",\"params\":").Append(BuildJsonValue(@params));
    sb.Append('}');
    return sb.ToString();
}

SendFrame(JsonRpc(1, "initialize", new Dictionary<string, object?>
{
    ["protocolVersion"] = "2024-11-05",
    ["capabilities"] = new Dictionary<string, object?>(),
    ["clientInfo"] = new Dictionary<string, object?>
    {
        ["name"] = "dotnet-e2e",
        ["version"] = "1.0"
    }
}));

var init = JsonDocument.Parse(ReadFrame());
Console.WriteLine("[3/5] Initialize response:");
Console.WriteLine(init.RootElement.GetRawText());

SendFrame(JsonRpc(null, "notifications/initialized"));
SendFrame(JsonRpc(2, "tools/list"));
var tools = JsonDocument.Parse(ReadFrame());
Console.WriteLine("[4/5] Tools response:");
Console.WriteLine(tools.RootElement.GetRawText());

SendFrame(JsonRpc(3, "tools/call", new Dictionary<string, object?>
{
    ["name"] = "get_status",
    ["arguments"] = new Dictionary<string, object?>()
}));
var status = JsonDocument.Parse(ReadFrame());
Console.WriteLine("[5/5] Status response:");
Console.WriteLine(status.RootElement.GetRawText());

var subject = "E2E smtp4dev smoke test";
var body = "This email was sent by the .NET end-to-end smoke test.";
var from = "sender@example.test";
var to = "recipient@example.test";

using var smtp = new SmtpClient("localhost", smtpPort)
{
    EnableSsl = false,
    DeliveryMethod = SmtpDeliveryMethod.Network
};

using var message = new MailMessage(from, to, subject, body);
smtp.Send(message);

Console.WriteLine("Sent smoke-test message to smtp4dev.");

var deadline = DateTime.UtcNow.AddSeconds(20);
while (DateTime.UtcNow < deadline)
{
    SendFrame(JsonRpc(4, "tools/call", new Dictionary<string, object?>
    {
        ["name"] = "read_email",
        ["arguments"] = new Dictionary<string, object?>
        {
            ["maxResults"] = 10,
            ["folder"] = "INBOX"
        }
    }));

    var inbox = JsonDocument.Parse(ReadFrame());
    var content = inbox.RootElement.GetProperty("result").GetProperty("content");
    var text = content[0].GetProperty("text").GetString();
    if (text?.Contains(subject, StringComparison.OrdinalIgnoreCase) == true)
    {
        Console.WriteLine("Email was found in INBOX via MCP read_email.");
        return;
    }

    Thread.Sleep(1000);
}

throw new Exception("The smoke-test message never appeared in INBOX via MCP read_email.");

sealed class Smtp4DevFixture : IAsyncDisposable
{
    private readonly INetwork network;
    private readonly IContainer container;

    public Smtp4DevFixture()
    {
        network = new NetworkBuilder()
            .WithName($"emailmcp-e2e-{Guid.NewGuid():N}")
            .Build();

        container = new ContainerBuilder()
            .WithImage("rnwood/smtp4dev:v3")
            .WithNetwork(network)
            .WithPortBinding(80, true)
            .WithPortBinding(25, true)
            .WithPortBinding(143, true)
            .WithEnvironment("ServerOptions__Urls", "http://*:80")
            .WithEnvironment("ServerOptions__HostName", "smtp4dev")
            .WithVolumeMount("emailmcp-smtp4dev-data", "/smtp4dev")
            .WithWaitStrategy(Wait.ForUnixContainer()
                .UntilHttpRequestIsSucceeded(request => request.ForPort(80))
                .UntilInternalTcpPortIsAvailable(25)
                .UntilInternalTcpPortIsAvailable(143))
            .Build();
    }

    public int SmtpPort => container.GetMappedPublicPort(25);
    public int ImapPort => container.GetMappedPublicPort(143);

    public Task StartAsync() => container.StartAsync();

    public async ValueTask DisposeAsync()
    {
        await container.DisposeAsync();
        await network.DisposeAsync();
    }
}