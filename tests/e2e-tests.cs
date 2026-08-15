using System.Collections;
using System.Diagnostics;
using System.Globalization;
using System.Net.Mail;
using System.Text;
using System.Text.Json;

var useCompose = args.Contains("--compose", StringComparer.OrdinalIgnoreCase);
var smtpPort = useCompose ? 2525 : 25;
var imapPort = useCompose ? 2143 : 143;
var directory = Directory.GetCurrentDirectory();
var composeFile = Path.Combine(directory, "docker-compose.e2e.yml");

if (useCompose)
{
    Console.WriteLine("[1/5] Starting smtp4dev via docker compose...");
    var up = new ProcessStartInfo("docker", $"compose -f \"{composeFile}\" up -d")
    {
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false
    };
    var p = Process.Start(up)!;
    var stdout = p.StandardOutput.ReadToEnd();
    var stderr = p.StandardError.ReadToEnd();
    p.WaitForExit();
    if (p.ExitCode != 0)
        throw new Exception($"docker compose failed:\n{stdout}\n{stderr}");
    Console.WriteLine(stdout.Trim());
}
else
{
    Console.WriteLine($"[1/5] Using existing smtp4dev on localhost:{smtpPort} / {imapPort}");
}

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
        Arguments = "run --project src/EmailMcp.csproj",
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

string ReadFrame()
{
    var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    while (true)
    {
        var line = proc.StandardOutput.ReadLine();
        if (line is null)
            throw new InvalidOperationException("Process closed while reading MCP frame");
        if (string.IsNullOrEmpty(line))
            break;
        var idx = line.IndexOf(':');
        if (idx <= 0)
            throw new InvalidOperationException($"Malformed MCP header: {line}");
        headers[line[..idx]] = line[(idx + 1)..].Trim();
    }

    if (!headers.TryGetValue("Content-Length", out var lengthText))
        throw new InvalidOperationException("Missing Content-Length header");

    var length = int.Parse(lengthText, CultureInfo.InvariantCulture);
    var buffer = new char[length];
    var read = 0;
    while (read < length)
    {
        var chunk = proc.StandardOutput.Read(buffer, read, length - read);
        if (chunk == 0)
            throw new InvalidOperationException("Process ended before full message body was read");
        read += chunk;
    }

    return new string(buffer);
}

void SendFrame(string json)
{
    var body = Encoding.UTF8.GetBytes(json);
    var frame = Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n");
    proc.StandardInput.BaseStream.Write(frame, 0, frame.Length);
    proc.StandardInput.BaseStream.Write(body, 0, body.Length);
    proc.StandardInput.BaseStream.Flush();
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
    foreach (var p in value.GetType().GetProperties())
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

if (useCompose)
{
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
}

Console.WriteLine("Current docker image status check completed. Use --compose for the full round-trip test.");