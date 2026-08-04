#!/usr/bin/env dotnet
#:property TargetFramework=net10.0
#:package Microsoft.Extensions.Hosting@10.0.0
#:package ModelContextProtocol@2.0.0
#:package MailKitSimplified.Receiver@2.14.0
#:package MailKitSimplified.Sender@2.14.0

using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using MailKit;
using MailKit.Search;
using MailKit.Security;
using MailKitSimplified.Receiver.Services;
using MailKitSimplified.Sender.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using MimeKit;
using MailKitSimplified.Receiver.Extensions;
using MailKitSimplified.Receiver.Models;

var builder = Host.CreateApplicationBuilder(args);

// MCP stdio transport must keep stdout clean — send all logs to stderr.
builder.Logging.ClearProviders();
builder.Logging.AddConsole(options =>
    options.LogToStandardErrorThreshold = LogLevel.Trace);

builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

await builder.Build().RunAsync();

namespace EmailMcp
{
    [McpServerToolType]
    public static partial class EmailTools
    {
        // ── read_email ───────────────────────────────────────────────────────

        [McpServerTool(Name = "read_email", ReadOnly = true)]
        [Description(
            "Read the newest messages in an IMAP folder, or one message when uniqueId " +
            "is supplied from search_emails / list results. Returns JSON with UniqueId, " +
            "MessageId, headers, Markdown body, and attachment metadata.")]
        public static async Task<string> ReadEmailAsync(
            [Description("Maximum number of messages to return (1-25). Ignored when uniqueId is set.")]
            int maxResults = 5,
            [Description("IMAP folder name. Default: INBOX.")]
            string folder = "INBOX",
            [Description("Optional UniqueId from search_emails to fetch a single message.")]
            uint? uniqueId = null,
            CancellationToken cancellationToken = default)
        {
            try
            {
                maxResults = ValidateMaxResults(maxResults);
                var settings = ImapSettings.LoadEmailReceiverOptions(folder);
                using var receiver = ImapReceiver.Create(settings);

                if (uniqueId is null)
                {
                    // Top(n) returns newest first; also fetch summaries for UniqueIds.
                    var summaries = await receiver.ReadMail
                        .Top(maxResults)
                        .ItemsForMimeMessages()
                        .GetMessageSummariesAsync(cancellationToken);

                    var results = new List<EmailResult>(summaries.Count);
                    foreach (var summary in summaries)
                    {
                        var mime = await receiver.ReadFrom(folder)
                            .Query(SearchQuery.Uids(new UniqueIdSet { summary.UniqueId }))
                            .ItemsForMimeMessages()
                            .GetMimeMessagesAsync(cancellationToken);

                        var msg = mime.FirstOrDefault();
                        if (msg is not null)
                            results.Add(ToEmail(msg, summary.UniqueId.Id));
                    }

                    return Ok(results);
                }

                var messages = await receiver.ReadFrom(folder)
                    .Query(SearchQuery.Uids(new UniqueIdSet { new UniqueId(uniqueId.Value) }))
                    .ItemsForMimeMessages()
                    .GetMimeMessagesAsync(cancellationToken);

                return Ok(messages.Select(m => ToEmail(m, uniqueId.Value)));
            }
            catch (Exception ex)
            {
                return Fail(ex);
            }
        }

        // ── search_emails ────────────────────────────────────────────────────

        [McpServerTool(Name = "search_emails", ReadOnly = true)]
        [Description(
            "Search an IMAP folder and return lightweight matching message metadata " +
            "(UniqueId, Date, From, To, Subject). searchField: all | subject | from | unread | read.")]
        public static async Task<string> SearchEmailsAsync(
            [Description("Search term. Required for all / subject / from.")]
            string? searchTerm = null,
            [Description("Field to search: all, subject, from, unread, or read.")]
            string searchField = "all",
            [Description("Maximum number of results (1-25).")]
            int maxResults = 10,
            [Description("IMAP folder name. Default: INBOX.")]
            string folder = "INBOX",
            CancellationToken cancellationToken = default)
        {
            try
            {
                maxResults = ValidateMaxResults(maxResults);
                var query = BuildSearchQuery(searchTerm, searchField);

                var settings = ImapSettings.LoadEmailReceiverOptions(folder);
                using var receiver = ImapReceiver.Create(settings);
                var summaries = await receiver.ReadFrom(folder)
                    .Query(query)
                    .ItemsForMimeMessages()
                    .GetMessageSummariesAsync(cancellationToken);

                // Newest first
                var results = summaries
                    .Reverse()
                    .Take(maxResults)
                    .Select(ToSearchResult);

                return Ok(results);
            }
            catch (Exception ex)
            {
                return Fail(ex);
            }
        }

        // ── list_folders ─────────────────────────────────────────────────────

        [McpServerTool(Name = "list_folders", ReadOnly = true)]
        [Description("List IMAP folders/mailboxes available on the server.")]
        public static async Task<string> ListFoldersAsync(
            CancellationToken cancellationToken = default)
        {
            try
            {
                var settings = ImapSettings.LoadEmailReceiverOptions();
                using var receiver = ImapReceiver.Create(settings);
                var names = await receiver.GetMailFolderNamesAsync(cancellationToken).ConfigureAwait(false);
                return Ok(new { folders = names });
            }
            catch (Exception ex)
            {
                return Fail(ex);
            }
        }

        // ── get_status ───────────────────────────────────────────────────────

        [McpServerTool(Name = "get_status", ReadOnly = true)]
        [Description(
            "Check IMAP (and optional SMTP) connectivity and report basic account status " +
            "without downloading messages.")]
        public static async Task<string> GetStatusAsync(
            CancellationToken cancellationToken = default)
        {
            try
            {
                var imap = ImapSettings.Load();
                var smtp = SmtpSettings.TryLoad();

                string? imapError = null;
                int? inboxCount = null;
                try
                {
                    var settings = ImapSettings.LoadEmailReceiverOptions();
                    using var receiver = ImapReceiver.Create(settings);
                    var summaries = await receiver.ReadMail.Top(1)
                        .GetMessageSummariesAsync(cancellationToken);
                    inboxCount = summaries.Count; // just proves connectivity; count is not total
                    _ = inboxCount;
                }
                catch (Exception ex)
                {
                    imapError = FriendlyMessage(ex);
                }

                return Ok(new
                {
                    imap = new
                    {
                        host = imap.Host,
                        port = imap.Port,
                        user = imap.UserName,
                        ok = imapError is null,
                        error = imapError
                    },
                    smtp = smtp is null
                        ? null
                        : new
                        {
                            host = smtp.Host,
                            port = smtp.Port,
                            user = smtp.UserName,
                            configured = true
                        },
                    sendAllowList = GetSendAllowList(),
                    sendEnabled = IsSendEnabled()
                });
            }
            catch (Exception ex)
            {
                return Fail(ex);
            }
        }

        // ── send_email ───────────────────────────────────────────────────────

        [McpServerTool(Name = "send_email", ReadOnly = false)]
        [Description(
            "Send an email via SMTP. Requires SMTP_* env vars and SEND_EMAIL_ENABLED=true. " +
            "Recipients must match SEND_ALLOW_LIST (comma-separated addresses or domains like *@example.com). " +
            "Returns success or a clear error; never throws to the model.")]
        public static async Task<string> SendEmailAsync(
            [Description("Recipient email address (required).")] string to,
            [Description("Email subject (required).")] string subject,
            [Description("Body text (plain or HTML).")] string body,
            [Description("true = treat body as HTML.")] bool isHtml = false,
            [Description("Optional CC addresses, comma-separated.")] string? cc = null,
            [Description("Optional BCC addresses, comma-separated.")] string? bcc = null,
            CancellationToken cancellationToken = default)
        {
            try
            {
                if (!IsSendEnabled())
                {
                    return Fail(
                        "Sending is disabled. Set SEND_EMAIL_ENABLED=true and configure " +
                        "SMTP_HOST / SMTP_USER / SMTP_PASSWORD (and optionally SEND_ALLOW_LIST).");
                }

                ArgumentException.ThrowIfNullOrWhiteSpace(to);
                ArgumentException.ThrowIfNullOrWhiteSpace(subject);

                var recipients = SplitAddresses(to)
                    .Concat(SplitAddresses(cc))
                    .Concat(SplitAddresses(bcc))
                    .ToList();

                if (recipients.Count == 0)
                    return Fail("At least one recipient is required.");

                var blocked = recipients.Where(a => !IsAllowedRecipient(a)).ToList();
                if (blocked.Count > 0)
                {
                    return Fail(
                        $"Recipient(s) not on SEND_ALLOW_LIST: {string.Join(", ", blocked)}. " +
                        "Add exact addresses or domain patterns like *@example.com.");
                }

                var smtp = SmtpSettings.Load();
                using var sender = SmtpSender
                    .Create($"{smtp.Host}:{smtp.Port}")
                    .SetCredential(smtp.UserName, smtp.Password!);

                var writer = sender.WriteEmail
                    .From(smtp.FromAddress ?? smtp.UserName)
                    .To(to).Cc(cc).Bcc(bcc)
                    .Subject(subject);

                writer = isHtml ? writer.BodyHtml(body) : writer.BodyText(body);

                await writer.SendAsync(cancellationToken);

                return Ok(new
                {
                    sent = true,
                    to,
                    subject,
                    message = "Email sent successfully."
                });
            }
            catch (Exception ex)
            {
                return Fail(ex);
            }
        }

        // ── draft_email ──────────────────────────────────────────────────────

        [McpServerTool(Name = "draft_email", ReadOnly = false)]
        [Description(
            "Open a pre-filled draft in the default email application via a mailto: URI. " +
            "This tool never sends email.")]
        public static string DraftEmail(
            [Description("Recipient email address.")] string to,
            [Description("Email subject.")] string subject,
            [Description("Email body (plain text).")] string body)
        {
            try
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(to);

                var mailtoUri =
                    $"mailto:{Uri.EscapeDataString(to)}" +
                    $"?subject={Uri.EscapeDataString(subject ?? "")}" +
                    $"&body={Uri.EscapeDataString(body ?? "")}";

                Process.Start(new ProcessStartInfo(mailtoUri)
                {
                    UseShellExecute = true
                });

                return Ok(new
                {
                    opened = true,
                    message = "A pre-filled email draft was opened in the default email application. It has not been sent."
                });
            }
            catch (Exception ex)
            {
                return Fail(ex);
            }
        }

        // ── helpers: connections ─────────────────────────────────────────────

        private static async Task<MailKit.Net.Imap.ImapClient> OpenImapClientAsync(CancellationToken cancellationToken)
        {
            var settings = ImapSettings.Load();
            var client = new MailKit.Net.Imap.ImapClient();
            await client.ConnectAsync(settings.Host, settings.Port, SecureSocketOptions.Auto, cancellationToken);

            if (!string.IsNullOrWhiteSpace(settings.AccessToken))
            {
                var oauth2 = new SaslMechanismOAuth2(settings.UserName, settings.AccessToken);
                await client.AuthenticateAsync(oauth2, cancellationToken);
            }
            else
            {
                await client.AuthenticateAsync(settings.UserName, settings.Password!, cancellationToken);
            }

            return client;
        }

        // ── helpers: search / validation ─────────────────────────────────────

        private static SearchQuery BuildSearchQuery(string? searchTerm, string searchField) =>
            searchField.Trim().ToLowerInvariant() switch
            {
                "all" when !string.IsNullOrWhiteSpace(searchTerm) =>
                    SearchQuery.BodyContains(searchTerm)
                        .Or(SearchQuery.SubjectContains(searchTerm))
                        .Or(SearchQuery.FromContains(searchTerm)),

                "subject" when !string.IsNullOrWhiteSpace(searchTerm) =>
                    SearchQuery.SubjectContains(searchTerm),

                "from" when !string.IsNullOrWhiteSpace(searchTerm) =>
                    SearchQuery.FromContains(searchTerm),

                "unread" => SearchQuery.NotSeen,
                "read"   => SearchQuery.Seen,
                "all"    => SearchQuery.All,

                _ => throw new ArgumentException(
                    "searchField must be all, subject, from, unread, or read. " +
                    "A search term is required for all, subject, and from.")
            };

        private static int ValidateMaxResults(int maxResults) =>
            maxResults is >= 1 and <= 25
                ? maxResults
                : throw new ArgumentOutOfRangeException(
                    nameof(maxResults),
                    "maxResults must be between 1 and 25.");

        // ── helpers: mapping ─────────────────────────────────────────────────

        private static EmailResult ToEmail(MimeMessage message, uint uniqueId)
        {
            var attachments = message.Attachments
                .Select(a =>
                {
                    var name = a is MimePart part
                        ? part.FileName ?? part.ContentType.Name ?? "attachment"
                        : "attachment";
                    var size = a is MimePart p && p.ContentDisposition?.Size is long s ? s : (long?)null;
                    return new AttachmentInfo(name, a.ContentType.MimeType, size);
                })
                .ToList();

            return new EmailResult(
                UniqueId: uniqueId,
                MessageId: message.MessageId ?? string.Empty,
                Date: message.Date,
                From: message.From.ToString(),
                To: message.To.ToString(),
                Cc: message.Cc.ToString(),
                Subject: message.Subject ?? string.Empty,
                MarkdownBody: ToMarkdown(message),
                Attachments: attachments);
        }

        private static SearchResult ToSearchResult(IMessageSummary summary) => new(
            summary.UniqueId.Id,
            summary.Envelope?.Date ?? default,
            summary.Envelope?.From.ToString() ?? string.Empty,
            summary.Envelope?.To.ToString() ?? string.Empty,
            summary.Envelope?.Subject ?? string.Empty);

        private static string ToMarkdown(MimeMessage message)
        {
            if (!string.IsNullOrWhiteSpace(message.TextBody))
                return message.TextBody.Trim();

            var html = message.HtmlBody;
            if (string.IsNullOrWhiteSpace(html))
                return string.Empty;

            html = ScriptAndStyleRegex().Replace(html, string.Empty);
            html = LineBreakRegex().Replace(html, "\n");
            html = ParagraphRegex().Replace(html, "\n\n");
            html = ListItemRegex().Replace(html, "\n- ");
            html = TagRegex().Replace(html, string.Empty);

            return WebUtility.HtmlDecode(html).Trim();
        }

        [GeneratedRegex(@"<(script|style)\b[^>]*>[\s\S]*?</\1>", RegexOptions.IgnoreCase)]
        private static partial Regex ScriptAndStyleRegex();

        [GeneratedRegex(@"<br\s*/?>", RegexOptions.IgnoreCase)]
        private static partial Regex LineBreakRegex();

        [GeneratedRegex(@"</?(p|div|h[1-6])\b[^>]*>", RegexOptions.IgnoreCase)]
        private static partial Regex ParagraphRegex();

        [GeneratedRegex(@"<li\b[^>]*>", RegexOptions.IgnoreCase)]
        private static partial Regex ListItemRegex();

        [GeneratedRegex(@"<[^>]+>")]
        private static partial Regex TagRegex();

        // ── helpers: send allow-list ─────────────────────────────────────────

        private static bool IsSendEnabled() =>
            string.Equals(
                Environment.GetEnvironmentVariable("SEND_EMAIL_ENABLED"),
                "true",
                StringComparison.OrdinalIgnoreCase);

        private static IReadOnlyList<string> GetSendAllowList()
        {
            var raw = Environment.GetEnvironmentVariable("SEND_ALLOW_LIST") ?? "";
            return raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(s => s.Length > 0)
                .ToList();
        }

        private static bool IsAllowedRecipient(string address)
        {
            var list = GetSendAllowList();
            // Empty allow-list = deny all (safe default when send is enabled).
            if (list.Count == 0)
                return false;

            address = address.Trim();
            foreach (var entry in list)
            {
                if (entry.StartsWith("*@", StringComparison.Ordinal))
                {
                    var domain = entry[1..]; // "@example.com"
                    if (address.EndsWith(domain, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
                else if (string.Equals(entry, address, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private static IEnumerable<string> SplitAddresses(string? value) =>
            string.IsNullOrWhiteSpace(value)
                ? []
                : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        // ── helpers: JSON results ────────────────────────────────────────────

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        private static string Ok(object payload) =>
            JsonSerializer.Serialize(new { ok = true, data = payload }, JsonOptions);

        private static string Fail(Exception ex) =>
            JsonSerializer.Serialize(new { ok = false, error = FriendlyMessage(ex) }, JsonOptions);

        private static string Fail(string message) =>
            JsonSerializer.Serialize(new { ok = false, error = message }, JsonOptions);

        private static string FriendlyMessage(Exception ex) =>
            ex switch
            {
                AuthenticationException =>
                    "IMAP/SMTP authentication failed. Check username, password, or access token " +
                    "(use an app password for Gmail/Microsoft).",
                SslHandshakeException =>
                    "TLS/SSL handshake failed. Verify host, port, and that the server supports TLS.",
                ArgumentOutOfRangeException or ArgumentException =>
                    ex.Message,
                InvalidOperationException =>
                    ex.Message,
                _ when ex.InnerException is not null =>
                    $"{ex.GetType().Name}: {ex.Message} ({ex.InnerException.Message})",
                _ =>
                    $"{ex.GetType().Name}: {ex.Message}"
            };

        // ── records ──────────────────────────────────────────────────────────

        private sealed record EmailResult(
            uint UniqueId,
            string MessageId,
            DateTimeOffset Date,
            string From,
            string To,
            string Cc,
            string Subject,
            string MarkdownBody,
            IReadOnlyList<AttachmentInfo> Attachments);

        private sealed record SearchResult(
            uint UniqueId,
            DateTimeOffset Date,
            string From,
            string To,
            string Subject);

        private sealed record AttachmentInfo(string FileName, string ContentType, long? Size);

        // ── settings ─────────────────────────────────────────────────────────

        internal sealed record ImapSettings(
            string Host,
            int Port,
            string UserName,
            string? Password,
            string? AccessToken)
        {
            public static EmailReceiverOptions LoadEmailReceiverOptions(string? folder = null)
            {
                var fileSettings = LoadSettingsFile();

                var userName    = GetSetting("IMAP_USER", fileSettings);
                var password    = GetSetting("IMAP_PASSWORD", fileSettings);
                var accessToken = GetSetting("IMAP_ACCESS_TOKEN", fileSettings);
                var host        = GetSetting("IMAP_HOST", fileSettings);
                var portText    = GetSetting("IMAP_PORT", fileSettings);

                var receiverOptions = new EmailReceiverOptions($"{host}:{portText}");
                if (!string.IsNullOrWhiteSpace(folder))
                {
                    receiverOptions.MailFolderName = folder;
                    receiverOptions.MailFolderAccess = FolderAccess.ReadOnly;
                }
                if (!string.IsNullOrWhiteSpace(userName) && !string.IsNullOrWhiteSpace(accessToken))
                {
                    receiverOptions.AuthenticationMechanism = new SaslMechanismOAuth2(userName, accessToken);
                }
                else if (!string.IsNullOrWhiteSpace(password))
                {
                    receiverOptions.ImapCredential = new NetworkCredential(userName, password);
                }

                return receiverOptions;
            }

            public static ImapSettings Load()
            {
                var fileSettings = LoadSettingsFile();

                var userName    = GetSetting("IMAP_USER", fileSettings);
                var password    = GetSetting("IMAP_PASSWORD", fileSettings);
                var accessToken = GetSetting("IMAP_ACCESS_TOKEN", fileSettings);

                if (string.IsNullOrWhiteSpace(userName) ||
                    (string.IsNullOrWhiteSpace(password) && string.IsNullOrWhiteSpace(accessToken)))
                {
                    throw new InvalidOperationException(
                        "Set IMAP_USER plus IMAP_ACCESS_TOKEN (recommended) or IMAP_PASSWORD " +
                        "before reading email.");
                }

                var host = GetSetting("IMAP_HOST", fileSettings);
                if (string.IsNullOrWhiteSpace(host) ||
                    Uri.CheckHostName(host) == UriHostNameType.Unknown)
                {
                    throw new InvalidOperationException(
                        "IMAP_HOST must be a host name or IP address without a protocol or port.");
                }

                var portText = GetSetting("IMAP_PORT", fileSettings);
                if (string.IsNullOrWhiteSpace(portText))
                {
                    throw new InvalidOperationException("IMAP_PORT must be specified.");
                }
                var port = string.IsNullOrWhiteSpace(portText)
                    ? 993
                    : int.TryParse(
                          portText,
                          System.Globalization.NumberStyles.None,
                          System.Globalization.CultureInfo.InvariantCulture,
                          out var configuredPort) &&
                      configuredPort is >= 1 and <= 65535
                        ? configuredPort
                        : throw new InvalidOperationException(
                            "IMAP_PORT must be an integer between 1 and 65535.");

                return new ImapSettings(host, port, userName, password, accessToken);
            }
        }

        internal sealed record SmtpSettings(
            string Host,
            int Port,
            string UserName,
            string Password,
            string? FromAddress)
        {
            public static SmtpSettings? TryLoad()
            {
                try { return Load(); }
                catch { return null; }
            }

            public static SmtpSettings Load()
            {
                var fileSettings = LoadSettingsFile();

                var host = GetSetting("SMTP_HOST", fileSettings)
                    ?? GetSetting("IMAP_HOST", fileSettings);
                var user = GetSetting("SMTP_USER", fileSettings)
                    ?? GetSetting("IMAP_USER", fileSettings);
                var pass = GetSetting("SMTP_PASSWORD", fileSettings)
                    ?? GetSetting("IMAP_PASSWORD", fileSettings);
                var from = GetSetting("SMTP_FROM", fileSettings) ?? user;

                if (string.IsNullOrWhiteSpace(host) ||
                    string.IsNullOrWhiteSpace(user) ||
                    string.IsNullOrWhiteSpace(pass))
                {
                    throw new InvalidOperationException(
                        "SMTP_HOST, SMTP_USER, and SMTP_PASSWORD are required to send email " +
                        "(or reuse IMAP_* values).");
                }

                var portText = GetSetting("SMTP_PORT", fileSettings);
                var port = string.IsNullOrWhiteSpace(portText)
                    ? 587
                    : int.TryParse(portText, out var p) && p is >= 1 and <= 65535
                        ? p
                        : 587;

                return new SmtpSettings(host, port, user, pass, from);
            }
        }

        private static string? GetSetting(
            string key,
            IReadOnlyDictionary<string, string> fileSettings) =>
            Environment.GetEnvironmentVariable(key)
            ?? fileSettings.GetValueOrDefault(key);

        private static IReadOnlyDictionary<string, string> LoadSettingsFile()
        {
            var path = Environment.GetEnvironmentVariable("IMAP_SETTINGS_FILE")
                ?? Environment.GetEnvironmentVariable("EMAIL_SETTINGS_FILE");
            if (string.IsNullOrWhiteSpace(path))
                return new Dictionary<string, string>(StringComparer.Ordinal);

            if (!File.Exists(path))
                throw new InvalidOperationException($"Settings file does not exist: {path}");

            var settings = new Dictionary<string, string>(StringComparer.Ordinal);
            var allowed = new HashSet<string>(StringComparer.Ordinal)
            {
                "IMAP_HOST", "IMAP_PORT", "IMAP_USER", "IMAP_PASSWORD", "IMAP_ACCESS_TOKEN",
                "SMTP_HOST", "SMTP_PORT", "SMTP_USER", "SMTP_PASSWORD", "SMTP_FROM",
                "SEND_EMAIL_ENABLED", "SEND_ALLOW_LIST"
            };

            foreach (var line in File.ReadLines(path))
            {
                var trimmed = line.Trim();
                if (string.IsNullOrWhiteSpace(trimmed) || trimmed.StartsWith('#'))
                    continue;

                var separator = trimmed.IndexOf('=');
                if (separator <= 0)
                    continue;

                var key = trimmed[..separator].Trim();
                if (allowed.Contains(key))
                    settings[key] = trimmed[(separator + 1)..].Trim();
            }

            return settings;
        }
    }
}
