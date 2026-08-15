using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using MailKit;
using MailKit.Search;
using MailKit.Security;
using MailKitSimplified.Receiver.Extensions;
using MailKitSimplified.Receiver.Models;
using MailKitSimplified.Receiver.Services;
using MailKitSimplified.Sender.Models;
using MailKitSimplified.Sender.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using MimeKit;

var builder = Host.CreateApplicationBuilder(args);

// Keep stdout clean for MCP stdio transport.
builder.Logging.ClearProviders();
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

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
            "is supplied. Returns JSON with UniqueId, MessageId, headers, Markdown body, " +
            "and attachment metadata.")]
        public static async Task<string> ReadEmailAsync(
            [Description("Maximum number of messages (1-25). Ignored when uniqueId is set.")]
            int maxResults = 5,
            [Description("IMAP folder. Default: INBOX.")]
            string folder = "INBOX",
            [Description("Optional UniqueId from search_emails.")]
            uint? uniqueId = null,
            CancellationToken ct = default)
        {
            try
            {
                maxResults = Clamp(maxResults);
                using var receiver = CreateReceiver(folder);

                if (uniqueId is null)
                {
                    var summaries = await receiver.ReadMail
                        .Top(maxResults)
                        .ItemsForMimeMessages()
                        .GetMessageSummariesAsync(ct);

                    var results = new List<EmailResult>(summaries.Count);
                    foreach (var s in summaries)
                    {
                        var msgs = await receiver.ReadFrom(folder)
                            .Query(SearchQuery.Uids(new UniqueIdSet { s.UniqueId }))
                            .ItemsForMimeMessages()
                            .GetMimeMessagesAsync(ct);
                        if (msgs.FirstOrDefault() is { } m)
                            results.Add(ToEmail(m, s.UniqueId.Id));
                    }
                    return Ok(results);
                }

                var messages = await receiver.ReadFrom(folder)
                    .Query(SearchQuery.Uids(new UniqueIdSet { new UniqueId(uniqueId.Value) }))
                    .ItemsForMimeMessages()
                    .GetMimeMessagesAsync(ct);

                return Ok(messages.Select(m => ToEmail(m, uniqueId.Value)));
            }
            catch (Exception ex) { return Fail(ex); }
        }

        // ── search_emails ────────────────────────────────────────────────────

        [McpServerTool(Name = "search_emails", ReadOnly = true)]
        [Description(
            "Search an IMAP folder. Returns UniqueId, Date, From, To, Subject. " +
            "searchField: all | subject | from | unread | read.")]
        public static async Task<string> SearchEmailsAsync(
            [Description("Search term (required for all/subject/from).")] string? searchTerm = null,
            [Description("Field: all, subject, from, unread, or read.")] string searchField = "all",
            [Description("Max results (1-25).")] int maxResults = 10,
            [Description("IMAP folder. Default: INBOX.")] string folder = "INBOX",
            CancellationToken ct = default)
        {
            try
            {
                maxResults = Clamp(maxResults);
                var query = BuildQuery(searchTerm, searchField);

                using var receiver = CreateReceiver(folder);
                var summaries = await receiver.ReadFrom(folder)
                    .Query(query)
                    .ItemsForMimeMessages()
                    .GetMessageSummariesAsync(ct);

                var results = summaries
                    .Reverse()
                    .Take(maxResults)
                    .Select(s => new SearchResult(
                        s.UniqueId.Id,
                        s.Envelope?.Date ?? default,
                        s.Envelope?.From.ToString() ?? "",
                        s.Envelope?.To.ToString() ?? "",
                        s.Envelope?.Subject ?? ""));

                return Ok(results);
            }
            catch (Exception ex) { return Fail(ex); }
        }

        // ── list_folders ─────────────────────────────────────────────────────

        [McpServerTool(Name = "list_folders", ReadOnly = true)]
        [Description("List IMAP folders/mailboxes on the server.")]
        public static async Task<string> ListFoldersAsync(CancellationToken ct = default)
        {
            try
            {
                using var receiver = CreateReceiver();
                var names = await receiver.GetMailFolderNamesAsync(ct);
                return Ok(new { folders = names });
            }
            catch (Exception ex) { return Fail(ex); }
        }

        // ── get_status ───────────────────────────────────────────────────────

        [McpServerTool(Name = "get_status", ReadOnly = true)]
        [Description("Check IMAP (and optional SMTP) connectivity without downloading messages.")]
        public static async Task<string> GetStatusAsync(CancellationToken ct = default)
        {
            try
            {
                var imapOpts = LoadReceiverOptions();
                var smtpOpts = TryLoadSenderOptions();

                string? imapError = null;
                try
                {
                    using var receiver = ImapReceiver.Create(imapOpts);
                    _ = await receiver.ReadMail.Top(1).GetMessageSummariesAsync(ct);
                }
                catch (Exception ex) { imapError = Friendly(ex); }

                string? smtpError = null;
                bool smtpConfigured = smtpOpts is not null;
                if (smtpConfigured)
                {
                    try
                    {
                        using var sender = SmtpSender.Create(smtpOpts);
                        // The act of creating and disposing the sender will connect and disconnect.
                    }
                    catch (Exception ex)
                    {
                        smtpError = Friendly(ex);
                    }
                }

                return Ok(new
                {
                    imap = new
                    {
                        host = imapOpts.ImapHost,
                        user = imapOpts.ImapCredential?.UserName,
                        ok = imapError is null,
                        error = imapError
                    },
                    smtp = smtpConfigured ? new
                    {
                        host = smtpOpts.SmtpHost,
                        user = smtpOpts.SmtpCredential?.UserName,
                        configured = true,
                        ok = smtpError is null,
                        error = smtpError
                    } : null,
                    sendAllowList = GetAllowList(),
                    sendEnabled = IsSendEnabled()
                });
            }
            catch (Exception ex) { return Fail(ex); }
        }

        // ── send_email ───────────────────────────────────────────────────────

        [McpServerTool(Name = "send_email", ReadOnly = false)]
        [Description(
            "Send via SMTP. Requires SMTP_* env vars and SEND_EMAIL_ENABLED=true. " +
            "Recipients must match SEND_ALLOW_LIST (addresses or *@domain.com).")]
        public static async Task<string> SendEmailAsync(
            [Description("Recipient (required).")] string to,
            [Description("Subject (required).")] string subject,
            [Description("Body (plain or HTML).")] string body,
            [Description("true = HTML body.")] bool isHtml = false,
            [Description("Optional CC, comma-separated.")] string? cc = null,
            [Description("Optional BCC, comma-separated.")] string? bcc = null,
            CancellationToken ct = default)
        {
            try
            {
                if (!IsSendEnabled())
                    return Fail("Sending disabled. Set SEND_EMAIL_ENABLED=true and SMTP_HOST/USER/PASSWORD (and optionally SEND_ALLOW_LIST).");

                ArgumentException.ThrowIfNullOrWhiteSpace(to);
                ArgumentException.ThrowIfNullOrWhiteSpace(subject);

                var recipients = Split(to).Concat(Split(cc)).Concat(Split(bcc)).ToList();
                if (recipients.Count == 0) return Fail("At least one recipient required.");

                var blocked = recipients.Where(a => !IsAllowed(a)).ToList();
                if (blocked.Count > 0)
                    return Fail($"Not on SEND_ALLOW_LIST: {string.Join(", ", blocked)}. Use exact addresses or *@example.com.");

                var opts = LoadSenderOptions();
                using var sender = SmtpSender.Create(opts);

                var writer = sender.WriteEmail
                    .From(opts.EmailWriter?.DefaultReplyToAddress ?? opts.SmtpCredential?.UserName ?? "")
                    .To(to).Cc(cc).Bcc(bcc)
                    .Subject(subject);

                writer = isHtml ? writer.BodyHtml(body) : writer.BodyText(body);
                await writer.SendAsync(ct);

                return Ok(new { sent = true, to, subject, message = "Email sent successfully." });
            }
            catch (Exception ex) { return Fail(ex); }
        }

        // ── draft_email ──────────────────────────────────────────────────────

        [McpServerTool(Name = "draft_email", ReadOnly = false)]
        [Description("Open a pre-filled draft in the default email app via mailto:. Never sends.")]
        public static string DraftEmail(
            [Description("Recipient.")] string to,
            [Description("Subject.")] string subject,
            [Description("Body (plain text).")] string body)
        {
            try
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(to);
                var uri = $"mailto:{Uri.EscapeDataString(to)}" +
                          $"?subject={Uri.EscapeDataString(subject ?? "")}" +
                          $"&body={Uri.EscapeDataString(body ?? "")}";

                Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
                return Ok(new { opened = true, message = "Draft opened in default email app (not sent)." });
            }
            catch (Exception ex) { return Fail(ex); }
        }

        // ── native options helpers ───────────────────────────────────────────

        private static ImapReceiver CreateReceiver(string? folder = null)
        {
            var opts = LoadReceiverOptions(folder);
            return ImapReceiver.Create(opts);
        }

        private static EmailReceiverOptions LoadReceiverOptions(string? folder = null)
        {
            var s = LoadSettings();
            var host = Req(s, "IMAP_HOST");
            var user = Req(s, "IMAP_USER");
            var pass = Get(s, "IMAP_PASSWORD");
            var token = Get(s, "IMAP_ACCESS_TOKEN");

            if (string.IsNullOrWhiteSpace(pass) && string.IsNullOrWhiteSpace(token))
                throw new InvalidOperationException("Set IMAP_PASSWORD or IMAP_ACCESS_TOKEN.");

            var opts = new EmailReceiverOptions(host)
            {
                MailFolderName = folder ?? "INBOX",
                MailFolderAccess = FolderAccess.ReadOnly
            };

            if (!string.IsNullOrWhiteSpace(token))
                opts.AuthenticationMechanism = new SaslMechanismOAuth2(user, token);
            else
                opts.ImapCredential = new NetworkCredential(user, pass);

            return opts;
        }

        private static EmailSenderOptions? TryLoadSenderOptions()
        {
            try { return LoadSenderOptions(); }
            catch { return null; }
        }

        private static EmailSenderOptions LoadSenderOptions()
        {
            var s = LoadSettings();
            var host = Get(s, "SMTP_HOST") ?? Get(s, "IMAP_HOST")
                ?? throw new InvalidOperationException("SMTP_HOST (or IMAP_HOST) required.");
            var user = Get(s, "SMTP_USER") ?? Get(s, "IMAP_USER")
                ?? throw new InvalidOperationException("SMTP_USER (or IMAP_USER) required.");
            var pass = Get(s, "SMTP_PASSWORD") ?? Get(s, "IMAP_PASSWORD")
                ?? throw new InvalidOperationException("SMTP_PASSWORD (or IMAP_PASSWORD) required.");
            var from = Get(s, "SMTP_FROM") ?? user;

            return new EmailSenderOptions(host)
            {
                SmtpCredential = new NetworkCredential(user, pass),
                EmailWriter = new EmailWriterOptions { DefaultReplyToAddress = from }
            };
        }

        // ── search / validation ──────────────────────────────────────────────

        private static SearchQuery BuildQuery(string? term, string field) =>
            field.Trim().ToLowerInvariant() switch
            {
                "all" when !string.IsNullOrWhiteSpace(term) =>
                    SearchQuery.BodyContains(term)
                        .Or(SearchQuery.SubjectContains(term))
                        .Or(SearchQuery.FromContains(term)),
                "subject" when !string.IsNullOrWhiteSpace(term) => SearchQuery.SubjectContains(term),
                "from" when !string.IsNullOrWhiteSpace(term) => SearchQuery.FromContains(term),
                "unread" => SearchQuery.NotSeen,
                "read" => SearchQuery.Seen,
                "all" => SearchQuery.All,
                _ => throw new ArgumentException(
                    "searchField must be all|subject|from|unread|read. Term required for all/subject/from.")
            };

        private static int Clamp(int n) =>
            n is >= 1 and <= 25 ? n
            : throw new ArgumentOutOfRangeException(nameof(n), "maxResults must be 1-25.");

        // ── mapping ──────────────────────────────────────────────────────────

        private static EmailResult ToEmail(MimeMessage m, uint uid)
        {
            var atts = m.Attachments.Select(a =>
            {
                var name = a is MimePart p ? p.FileName ?? p.ContentType.Name ?? "attachment" : "attachment";
                long? size = a is MimePart mp && mp.ContentDisposition?.Size is long s ? s : null;
                return new AttachmentInfo(name, a.ContentType.MimeType, size);
            }).ToList();

            return new EmailResult(
                uid, m.MessageId ?? "", m.Date,
                m.From.ToString(), m.To.ToString(), m.Cc.ToString(),
                m.Subject ?? "", ToMarkdown(m), atts);
        }

        private static string ToMarkdown(MimeMessage m)
        {
            if (!string.IsNullOrWhiteSpace(m.TextBody)) return m.TextBody.Trim();
            var html = m.HtmlBody;
            if (string.IsNullOrWhiteSpace(html)) return "";

            html = ScriptStyle().Replace(html, "");
            html = Br().Replace(html, "\n");
            html = Para().Replace(html, "\n\n");
            html = Li().Replace(html, "\n- ");
            html = Tag().Replace(html, "");
            return WebUtility.HtmlDecode(html).Trim();
        }

        [GeneratedRegex(@"<(script|style)\b[^>]*>[\s\S]*?</\1>", RegexOptions.IgnoreCase)]
        private static partial Regex ScriptStyle();
        [GeneratedRegex(@"<br\s*/?>", RegexOptions.IgnoreCase)]
        private static partial Regex Br();
        [GeneratedRegex(@"</?(p|div|h[1-6])\b[^>]*>", RegexOptions.IgnoreCase)]
        private static partial Regex Para();
        [GeneratedRegex(@"<li\b[^>]*>", RegexOptions.IgnoreCase)]
        private static partial Regex Li();
        [GeneratedRegex(@"<[^>]+>")]
        private static partial Regex Tag();

        // ── allow-list ───────────────────────────────────────────────────────

        private static bool IsSendEnabled() =>
            string.Equals(Environment.GetEnvironmentVariable("SEND_EMAIL_ENABLED"), "true",
                StringComparison.OrdinalIgnoreCase);

        private static IReadOnlyList<string> GetAllowList()
        {
            var raw = Environment.GetEnvironmentVariable("SEND_ALLOW_LIST") ?? "";
            return raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(x => x.Length > 0).ToList();
        }

        private static bool IsAllowed(string address)
        {
            var list = GetAllowList();
            if (list.Count == 0) return false; // empty = deny all
            address = address.Trim();
            foreach (var e in list)
            {
                if (e.StartsWith("*@", StringComparison.Ordinal) &&
                    address.EndsWith(e[1..], StringComparison.OrdinalIgnoreCase))
                    return true;
                if (string.Equals(e, address, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        private static IEnumerable<string> Split(string? v) =>
            string.IsNullOrWhiteSpace(v) ? []
            : v.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        // ── settings ─────────────────────────────────────────────────────────

        private static IReadOnlyDictionary<string, string> LoadSettings()
        {
            var path = Environment.GetEnvironmentVariable("EMAIL_SETTINGS_FILE");
            var dict = new Dictionary<string, string>(StringComparer.Ordinal);
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return dict;

            var allowed = new HashSet<string>(StringComparer.Ordinal)
            {
                "IMAP_HOST","IMAP_USER","IMAP_PASSWORD","IMAP_ACCESS_TOKEN",
                "SMTP_HOST","SMTP_USER","SMTP_PASSWORD","SMTP_FROM",
                "SEND_EMAIL_ENABLED","SEND_ALLOW_LIST"
            };

            foreach (var line in File.ReadLines(path))
            {
                var t = line.Trim();
                if (string.IsNullOrWhiteSpace(t) || t.StartsWith('#')) continue;
                var i = t.IndexOf('=');
                if (i <= 0) continue;
                var k = t[..i].Trim();
                if (allowed.Contains(k)) dict[k] = t[(i + 1)..].Trim();
            }
            return dict;
        }

        private static string? Get(IReadOnlyDictionary<string, string> s, string key) =>
            Environment.GetEnvironmentVariable(key) ?? s.GetValueOrDefault(key);

        private static string Req(IReadOnlyDictionary<string, string> s, string key) =>
            Get(s, key) ?? throw new InvalidOperationException($"{key} is required.");

        // ── JSON ─────────────────────────────────────────────────────────────

        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        private static string Ok(object data) =>
            JsonSerializer.Serialize(new { ok = true, data }, JsonOpts);

        private static string Fail(Exception ex) =>
            JsonSerializer.Serialize(new { ok = false, error = Friendly(ex) }, JsonOpts);

        private static string Fail(string msg) =>
            JsonSerializer.Serialize(new { ok = false, error = msg }, JsonOpts);

        private static string Friendly(Exception ex) => ex switch
        {
            AuthenticationException =>
                "Auth failed. Check username/password or access token (use an app password for Gmail/Microsoft).",
            SslHandshakeException =>
                "TLS/SSL handshake failed. Verify host, port, and TLS support.",
            ArgumentOutOfRangeException or ArgumentException or InvalidOperationException =>
                ex.Message,
            _ when ex.InnerException is not null =>
                $"{ex.GetType().Name}: {ex.Message} ({ex.InnerException.Message})",
            _ => $"{ex.GetType().Name}: {ex.Message}"
        };

        // ── records ──────────────────────────────────────────────────────────

        private sealed record EmailResult(
            uint UniqueId, string MessageId, DateTimeOffset Date,
            string From, string To, string Cc, string Subject,
            string MarkdownBody, IReadOnlyList<AttachmentInfo> Attachments);

        private sealed record SearchResult(
            uint UniqueId, DateTimeOffset Date, string From, string To, string Subject);

        private sealed record AttachmentInfo(string FileName, string ContentType, long? Size);
    }
}
