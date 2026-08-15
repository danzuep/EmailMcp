using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MailKitSimplified.Receiver.Models;
using MailKitSimplified.Sender.Models;
using MailKitSimplified.Receiver.Services;
using MailKitSimplified.Sender.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace EmailMcp
{
    public class EmailService
    {
        private readonly EmailReceiverOptions _receiverOptions;
        private readonly EmailSenderOptions? _senderOptions;
        private readonly ILogger<EmailService> _logger;

        public EmailService(IOptions<EmailReceiverOptions>? receiverOptions, IOptions<EmailSenderOptions>? senderOptions = null, ILogger<EmailService>? logger = null)
        {
            _receiverOptions = receiverOptions?.Value ?? new EmailReceiverOptions("localhost");
            _senderOptions = senderOptions?.Value ?? new EmailSenderOptions("localhost");
            _logger = logger ?? NullLogger<EmailService>.Instance;
        }

        public ImapReceiver CreateReceiver(string? folder = null)
        {
            var opts = GetReceiverOptions(folder);
            var imapReceiver = ImapReceiver.Create(opts);
            return imapReceiver;
        }

        public SmtpSender CreateSender()
        {
            var opts = GetSenderOptions();
            var smtpSender = SmtpSender.Create(opts);
            return smtpSender;
        }

        public EmailReceiverOptions GetReceiverOptions(string? folder = null)
        {
            var opts = new EmailReceiverOptions(_receiverOptions.ImapHost)
            {
                MailFolderName = folder ?? _receiverOptions.MailFolderName ?? "INBOX",
                MailFolderAccess = _receiverOptions.MailFolderAccess
            };
            if (_receiverOptions.AuthenticationMechanism is not null)
                opts.AuthenticationMechanism = _receiverOptions.AuthenticationMechanism;
            if (_receiverOptions.ImapCredential is not null)
                opts.ImapCredential = _receiverOptions.ImapCredential;
            return opts;
        }

        public EmailSenderOptions GetSenderOptions()
        {
            if (_senderOptions is null) throw new InvalidOperationException("EmailSenderOptions not configured.");
            return _senderOptions;
        }
    }
}
