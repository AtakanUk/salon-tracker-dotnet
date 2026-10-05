using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;
using SalonTracker.Api.Configuration;

namespace SalonTracker.Api.Infrastructure;

public sealed record MailResult(bool Sent, string? Error = null);

/// <summary>Alert and backup mails. Optional: without SMTP settings every send is a no-op.</summary>
public sealed class Mailer(AppSettings settings, ILogger<Mailer> logger)
{
    public bool Configured => settings.Smtp.Configured;

    public async Task<MailResult> SendAsync(string subject, string text, IEnumerable<string>? attachmentPaths = null)
    {
        if (!Configured) return new MailResult(false, "mail_not_configured");

        var smtp = settings.Smtp;
        var body = new BodyBuilder { TextBody = text };
        foreach (var path in attachmentPaths ?? []) await body.Attachments.AddAsync(path);

        var message = new MimeMessage
        {
            Subject = subject,
            Body = body.ToMessageBody(),
        };
        message.From.Add(MailboxAddress.Parse(smtp.From != "" ? smtp.From : smtp.User));
        message.To.Add(MailboxAddress.Parse(smtp.AlertTo));

        try
        {
            using var client = new SmtpClient();
            var security = smtp.Port == 465 ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTlsWhenAvailable;
            await client.ConnectAsync(smtp.Host, smtp.Port, security);
            await client.AuthenticateAsync(smtp.User, smtp.Password);
            await client.SendAsync(message);
            await client.DisconnectAsync(true);
            return new MailResult(true);
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Mail '{Subject}' could not be sent", subject);
            return new MailResult(false, e.Message);
        }
    }
}
