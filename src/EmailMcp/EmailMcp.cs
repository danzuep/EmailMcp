#!/usr/bin/env dotnet
#:property TargetFramework=net10.0
#:package Microsoft.Extensions.Hosting@10.0.3
#:package ModelContextProtocol@2.0.0
#:package MailKitSimplified.Receiver@2.14.0
#:package MailKitSimplified.Sender@2.14.0

using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Text. Json;
using System.Text.RegularExpressions;
using MailKit;
using MailKit.Search;
using MailKit.Security;
using MailKitSimplified.Receiver.Extensions;
using MailKitSimplified.Receiver.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using MimeKit;

var builder = Host.CreateApplicationBuilder(args);
builder.logging.clearProviders();
builder.logging.Addconsole(options => options.logTostandardErrorThreshold = Loglevel.Trace);
builder.services
    .AddMcpServer()
    .WithstdioserverTransport()
    .WithToolsFromAssembly();
await builder.Build().RunAsync();

namespace EmailMcp
{
[McpServerToolType]
public static partial class EmailTools
{
[McpServer Tool (Name = "read _email", Readonly = true)]
[Description("Read the newest messages in an IMAP folder, or one message when uniqueId is supplied from
search_emails. Returns each message as Markdown when a text Or HTML body is available.")]
public static async Task<string> ReadEmailAsync(
int maxResults = 5,
string folder = "INBOX"
uint? uniquerd - null,
cancellationToken cancellationToken = default)
maxResults - ValidateMaxResults (maxResults);
using var receiver = CreateReceiver(folder);
var messages = uniqueId is null
? await receiver.ReadMail.Top(maxResults). GetMimeMessagesAsync(cancellationToken)
await receiver.Readfrom(folder)
⁃ Query(SearchQuery.Uids([new uniqueId(uniqueId.Value)]))
.ItemsforMimeMessages()
.GetrinelessagesAsync(cancellat ionToken);
return Jsonserializer.serialize(messages.select (ToEmail), JsonOptions);
}

[McpserverTool (Name = "search _ emails", Readonly = true)]
[Description("search an IMAP folder and return lightweight matching mersage metadata. Search fields: all, subject,
from, unread, or read, The implementation uses Mailkit searchQuery through Mailkitsimplified's Query method.")]
references
public static async Tasksstring> SearchEmailsAsync(
string? searchTerm - null,
string searchfield - "all"
int maxResults- 10,
string folder = "INBOX"
cancellationToken cancellationToken " default)
maxResults ValidateMaxResults(maxResults);
var query - BuildsearchQuery(searchTerm, searchfield);

using var receiver = CreateReceiver(folder);
var 'summaries = await receiver.ReadFrom(folder)
Query(query)
•ItemsForMimeMessages()
.GetHessagesummariesAsync(cancellationToken))
return Jsonserializer.serialize(summaries, Take(maxResults).select(TosearchResult), Jsonoptions);
}

[McpserverTool (Name = "draft_ email", Readonly - false)]
[Description("open a pre-filled draft in the Windows default email application. This tool never sends email.")]
public static string DraftEmail(string to, string subject, string body)
{
ArgumentException.ThrowrfNullorwhitespace(to);
var mailtouri = $"mailto:(uri.EscapeDatastring(to)]?subject-(uri.EscapeDatastring(subject)]&bady-(uri.
EscapeDatastring(body)]";
Process.Start(new ProcessstartInfo(mailtouri) ( Useshellexecute = true ]):
return "A pre-filled email draft was opened in the default email appTication.
It has not been sent."
}

private static ImapReceiver CreateReceiver(string folder)
{
var settings = Imapsettings.load(),
var receiver = ImapReceiver.Create(;"(settings.Host):(settings,Port)").setFolder(folder);
if (!string,Is₩ullorkhítespace(settings,AccessToken))
var oauth2 = new saslMechanismoAuth2 (settings, UserName, settings.AccessToken):
return receiver,setcustomAuthentication(elient => elient AuthenticateAsync(oauth2)):
return receiver.setcredential(settings,userName, séttings.Password!);
}

private static SearchQuery BuildsearchQuery (string? searchrerm, string searchrield) =>
searchField.Trim() .ToLowerInvariant () switch
t
"all" when !string.IsNullorwhiteSpace (searchTerm) => SearchQuery.Bodycontains (searchTerm)
. Or (SearchQuery. SubjectContains (searchTerm))
. Or (SearchQuery . FromContains (searchTerm))
"subject" when !string.IsNullOrWhitespace (searchTerm) => searchQuery.subjectcontains (searchTerm)
"from" when !string.IsNullOrwhiteSpace (searchTerm) => SearchQuery.FromContains (searchTern)
"unread" => SearchQuery.NotSeen,
"read" => SearchQuery.Seen,
"all" => SearchQuery.All,
=> throw new ArgumentException ("searchField must be all, subject, from, unread, or read. A search term is required for all, subject, and from."),
private static int ValidateMaxResults (int maxResults) =>
maxResults is>= 1 and <= 25
?
maxResults
throw new ArgumentOutofRangeException (nameof (maxResults)
1
"maxResults must be between 1 and 25.");
private static EmailResult ToEmail (MimeMessage message) .=> new(
message.MessageId ?? string.Empty,
message.Date,
message.From.ToString() ,
message.To.ToString(),
message.Subject ?? string.Empty,
ToMarkdown (message));
private static SearchResult ToSearchResult (IMessageSummary summary)
summary.UniqueId.Id,
summary.Envelope?.Date ?? default
summary .Envelope?.From.ToString() ?? string.Empty,
summary.Envelope? .To.ToString() ?? string.Empty,
summary.Envelope?.Subject ?? string.Empty);
new(

private static string ToMarkdown (MimeMessage message)
{
if (!string. IsNullorhitespace (message.TextBody))
{
return message. TextBody Trim();
}

message.HtmlBody;
var htm]
if (string.IsNullorwhitespace (html))
return string.Empty;
html = scriptAndstyleRegex () .Replace (html, string.Empty);
html = LineBreakRegex() .Replace (html, "In");
html = ParagraphRegex () .Replace (html, "In\n");
html = ListItemRegex () .Replace (html, "In- ");
html= TagRegex().Replace (html, string.Empty);
return Webūtility.HtmlDecode (html) .Trim();
[GeneratedRegex ("<(script/style)\\b[^>]*>[\\s\\5]+zc/\\1>", Regexoptions. Ignorecase)]
private static partial Regex ScriptAndstyleRegex ();
[GeneratedRegex ("<hi\\s"/?>", Regexoptions.Ignorecase)]
private static partial Regex LineBreakRegex();
[GeneratedRegex ("</? (pidivlh(i-6])\\b[^>]*>", Regexoptions.IgnoreCase)]
private static partial Regex ParagraphRegex();
[GeneratedRegex ("<li\\b[^>]">", Regexoptions.Ignorecase)]
private static partial Regex ListItemRegex();
[GeneratedRegex ("<[^>]+>")]
private static partial Regex TagRegex();
private static readonly Jsonserializeroptions Jsonoptions = new() ( WriteIndented = true );
private sealed record EmailResult (string MessageId, DateTimeoffset Date, string From, string To, string Subject, atring MarkdownBody);
private sealed record searchResult (uint UniqueId, DateTimeoffset Date, string From, string To, atring Subject),
internal sealed record Imapsettings (string Host, int Port, string UserName, string? Password, atring? AccesaTaken)
public static Imapsettings Load()
var fileSettings = LoadsettingsFile();
var userName = GetSetting("IMAP USER", filesettings);
var password = Getsetting("IMAF_ PASSWORD", filesettings);
var accessToken = Getsetting("IMAP ACCESS_TOKEN", filesettings);
i£ (string. IsNullor₩nitespace (userName) |1 (string.IeNullorwhitespace (password) ss string.IsNullox₩hitaspace (acceasToken)))
t
throw new InvalidoperationEzception(
"Set IMAP_USER plus IMAP_ACCESS TOKEN (recomnended) Or IMAP_PASSWORD before reading emak) ")
var host = Getsetting("IMAP HOST", filesettings);
if (string.IsNullorwhitespace(host) || Uri.ChecktostName (host) == UriHostNameType.Unknoun)
throw ne₩ InvalidoperationException ("IMAP HOST must be a host name or IP address without a protocnl or port.")

var portText = GetSetting("IMAP PORT", filesettings);
var port = string.IsNullOrwhitespace (portText) ? 992 : int.TryParse (portText, system.Globalization Numberstyles.None
System.Globalization.CultureInfo.Invariantculture, out var configuredPort) ss configuredPort is >= 1 and <= 65535
configuredPort
throw new InvalidoperationException ("IMAP PORT must be an integer between 1 and 65530
return new Imapsettings (host, port, userName, password, accessToken);
private static string? Getsetting(string key, IReadonlyDictionary<string, string> filesettings)
Environment .GetEnvironmentvariable (key) ?? filesettings.GetvalueorDefault (key)
private static IReadonlyDictionary<string, string> LoadsettingsFile ()
var path = Environment .GetEnvironmentVariable ("IMAP SETTINGS FILE")
if (string.IsNullorwhitespace (path))
f
return new Dictionary<string, string>(StringComparer.Ordinal)
1
if (!File. Exists (path))
throw new InvalidoperationException ($"IMAP SETTINGS FILE does not
var settings = new Dictionary<string, string>(stringComparer.Ordinal)
foreach (var line in File.Readlines (path))
var trimed = line.Trim();
if (string.IsNullorwhitespace (trimmed) || trimmed.Starts₩ith('t'))
continue
var separator = trimmed.Indexof("=');
it (separator f
thzow new InvalidoperationException (S"IMAP SETTINGS FILE contains an invalid antting: (linel").
var key = trimmed[..separator] .Trim();
i£ (key is "IMAP_HOST" or "IMAP PORT" Or "IMAP USER" or "IMAP PASSHORD" or "IMAF ACCESS TOKEN
L
settings[key] = trimed[(separator + 1)..];
return settings;
}
}
}