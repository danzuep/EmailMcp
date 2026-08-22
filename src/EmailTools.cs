using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using MailKit;
using MailKit.Search;
using MailKit.Security;
using MailKitSimplified.Receiver.Abstractions;
using MailKitSimplified.Receiver.Extensions;
using MailKitSimplified.Receiver.Services;
using MailKitSimplified.Sender.Abstractions;
using MimeKit;
using ModelContextProtocol.Server;
using Serilog;

namespace EmailMcp
{
    [McpServerToolType]
    public static partial class EmailTools
    {
        private static class Metrics
        {
            private static readonly ConcurrentDictionary<string, long> Counts = new();
            private static readonly ConcurrentDictionary<string, long> TotalMs = new();
            public static void Record(string name, TimeSpan elapsed)
            {
                Counts.AddOrUpdate(name, 1, (_, v) => v + 1);
                TotalMs.AddOrUpdate(name, (long)elapsed.TotalMilliseconds, (_, v) => v + (long)elapsed.TotalMilliseconds);
                if (Counts.TryGetValue(name, out var c) && TotalMs.TryGetValue(name, out var t))
                    Log.Debug("Metric {Name}: count={Count} totalMs={TotalMs}", name, c, t);
            }
        }

        // ── read_email ───────────────────────────────────────────────────────

        [McpServerTool(Name = "read_email", ReadOnly = true)]
        [Description(
            "Read the newest messages in an IMAP folder, or one message when uniqueId " +
            "is supplied. Returns JSON with UniqueId, MessageId, headers, Markdown body, " +
            "and attachment metadata.")]
        public static async Task<string> ReadEmailAsync(
            EmailService emailService,
            [Description("Maximum number of messages (1-25). Ignored when uniqueId is set.")]
            int maxResults = 5,
            [Description("IMAP folder. Default: INBOX.")]
            string folder = "INBOX",
            [Description("Optional UniqueId from search_emails.")]
            uint? uniqueId = null,
            CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(emailService);
            var sw = Stopwatch.StartNew();
            try
            {
                using var imapReceiver = emailService.CreateReceiver();
                maxResults = Clamp(maxResults);

                if (uniqueId is null)
                {
                    IList<IMessageSummary> summaries;
                    try
                    {
                        summaries = await imapReceiver.ReadMail
                            .Top(maxResults)
                            .ItemsForMimeMessages()
                            .GetMessageSummariesAsync(ct);
                    }
                    catch (ArgumentOutOfRangeException ex) when (ex.ParamName == "min")
                    {
                        summaries = Array.Empty<IMessageSummary>();
                    }

                    var results = new List<EmailResult>(summaries.Count);
                    foreach (var s in summaries)
                    {
                        var msgs = await imapReceiver.ReadFrom(folder)
                            .Query(SearchQuery.Uids(new UniqueIdSet { s.UniqueId }))
                            .ItemsForMimeMessages()
                            .GetMimeMessagesAsync(ct);
                        if (msgs.FirstOrDefault() is { } m)
                            results.Add(ToEmail(m, s.UniqueId.Id));
                    }
                    return Ok(results);
                }

                var messages = await imapReceiver.ReadFrom(folder)
                    .Query(SearchQuery.Uids(new UniqueIdSet { new UniqueId(uniqueId.Value) }))
                    .ItemsForMimeMessages()
                    .GetMimeMessagesAsync(ct);

                return Ok(messages.Select(m => ToEmail(m, uniqueId.Value)));
            }
            catch (Exception ex) { return Fail(ex); }
            finally { Metrics.Record("read_email", sw.Elapsed); }
        }

        // ── search_emails ────────────────────────────────────────────────────

        [McpServerTool(Name = "search_emails", ReadOnly = true)]
        [Description(
            "Search an IMAP folder. Returns UniqueId, Date, From, To, Subject. " +
            "searchField: all | subject | from | unread | read.")]
        public static async Task<string> SearchEmailsAsync(
            EmailService emailService,
            [Description("Search term (required for all/subject/from).")] string? searchTerm = null,
            [Description("Field: all, subject, from, unread, or read.")] string searchField = "all",
            [Description("Max results (1-25).")] int maxResults = 10,
            [Description("IMAP folder. Default: INBOX.")] string folder = "INBOX",
            CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(emailService);
            var sw = Stopwatch.StartNew();
            try
            {
                using var imapReceiver = emailService.CreateReceiver();
                maxResults = Clamp(maxResults);
                var query = BuildQuery(searchTerm, searchField);

                var summaries = await imapReceiver.ReadFrom(folder)
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
            finally { Metrics.Record("search_emails", sw.Elapsed); }
        }

        // ── list_folders ─────────────────────────────────────────────────────

        [McpServerTool(Name = "list_folders", ReadOnly = true)]
        [Description("List IMAP folders/mailboxes on the server.")]
        public static async Task<string> ListFoldersAsync(
            EmailService emailService,
            CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(emailService);
            var sw = Stopwatch.StartNew();
            try
            {
                using var imapReceiver = emailService.CreateReceiver();
                var names = await imapReceiver.GetMailFolderNamesAsync(ct);
                return Ok(new { folders = names });
            }
            catch (Exception ex) { return Fail(ex); }
            finally { Metrics.Record("list_folders", sw.Elapsed); }
        }

        // ── get_status ───────────────────────────────────────────────────────

        [McpServerTool(Name = "get_status", ReadOnly = true)]
        [Description("Check IMAP connectivity without downloading messages.")]
        public static async Task<string> GetStatusAsync(
            EmailService emailService,
            CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(emailService);
            var sw = Stopwatch.StartNew();
            try
            {
                using var imapReceiver = emailService.CreateReceiver();
                string? imapError = null;
                try
                {
                    _ = await imapReceiver.ConnectAuthenticatedImapClientAsync(ct);
                }
                catch (Exception ex)
                {
                    imapError = Friendly(ex);
                }

                return Ok(new
                {
                    imap = new
                    {
                        ok = imapError is null,
                        error = imapError
                    },
                    sendAllowList = GetAllowList(),
                    sendEnabled = IsSendEnabled()
                });
            }
            catch (Exception ex) { return Fail(ex); }
            finally { Metrics.Record("get_status", sw.Elapsed); }
        }

        // ── send_email ───────────────────────────────────────────────────────

        [McpServerTool(Name = "send_email", ReadOnly = false)]
        [Description(
            "Send via SMTP. Requires SMTP_* env vars and SEND_EMAIL_ENABLED=true. " +
            "Recipients must match SEND_ALLOW_LIST (addresses or *@domain.com).")]
        public static async Task<string> SendEmailAsync(
            EmailService emailService,
            [Description("Recipient (required).")] string to,
            [Description("Subject (required).")] string subject,
            [Description("Body (plain or HTML).")] string body,
            [Description("true = HTML body.")] bool isHtml = false,
            [Description("Optional CC, comma-separated.")] string? cc = null,
            [Description("Optional BCC, comma-separated.")] string? bcc = null,
            CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(emailService);
            var sw = Stopwatch.StartNew();
            try
            {
                using var smtpSender = emailService.CreateSender();
                if (!IsSendEnabled())
                    return Fail("Sending disabled. Set SEND_EMAIL_ENABLED=true and SMTP_HOST/USER/PASSWORD (and optionally SEND_ALLOW_LIST).");

                ArgumentException.ThrowIfNullOrWhiteSpace(to);
                ArgumentException.ThrowIfNullOrWhiteSpace(subject);

                var recipients = Split(to).Concat(Split(cc)).Concat(Split(bcc)).ToList();
                if (recipients.Count == 0) return Fail("At least one recipient required.");

                var blocked = recipients.Where(a => !IsAllowed(a)).ToList();
                if (blocked.Count > 0)
                    return Fail($"Not on SEND_ALLOW_LIST: {string.Join(", ", blocked)}. Use exact addresses or *@example.com.");

                var writer = smtpSender.WriteEmail
                    .To(to).Cc(cc).Bcc(bcc)
                    .Subject(subject);

                writer = isHtml ? writer.BodyHtml(body) : writer.BodyText(body);
                await writer.SendAsync(ct);

                return Ok(new { sent = true, to, subject, message = "Email sent successfully." });
            }
            catch (Exception ex) { return Fail(ex); }
            finally { Metrics.Record("send_email", sw.Elapsed); }
        }

        // ── draft_email ──────────────────────────────────────────────────────

        [McpServerTool(Name = "draft_email", ReadOnly = false)]
        [Description("Open a pre-filled draft in the default email app via mailto:. Never sends.")]
        public static string DraftEmail(
            [Description("Recipient.")] string to,
            [Description("Subject.")] string subject,
            [Description("Body (plain text).")] string body)
        {
            var sw = Stopwatch.StartNew();
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
            finally { Metrics.Record("draft_email", sw.Elapsed); }
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

        // LoadSettings removed - configuration is provided via DI (IOptions<EmailReceiverOptions>/EmailSenderOptions)

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
