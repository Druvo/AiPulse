using System.Net;
using System.Net.Mail;

namespace AiPulse.Services;

/// <summary>
/// Optional email delivery, mirroring ISummarizer's "standalone by default" shape. AiPulse never requires
/// SMTP to function - password-reset links are always generated, just delivered differently depending on
/// whether Smtp:Host is configured (see Program.cs's conditional registration).
/// </summary>
public interface IEmailSender
{
    /// <summary>Whether a real SMTP backend is actually wired up.</summary>
    bool Enabled { get; }

    Task SendAsync(string toEmail, string subject, string bodyText, CancellationToken ct = default);
}

/// <summary>
/// Default no-op implementation: logs the message instead of sending it, so a self-hoster with no SMTP
/// configured still sees the full password-reset link directly in their console/server log. Password reset
/// itself works either way - Users.razor's "Generate reset link" button is the other route to the same URL,
/// for an admin to hand to a user manually.
/// </summary>
public sealed class NullEmailSender : IEmailSender
{
    private readonly ILogger<NullEmailSender> _log;
    public NullEmailSender(ILogger<NullEmailSender> log) => _log = log;

    public bool Enabled => false;

    public Task SendAsync(string toEmail, string subject, string bodyText, CancellationToken ct = default)
    {
        _log.LogWarning("Email not sent (no SMTP configured) - To: {To}, Subject: {Subject}\n{Body}", toEmail, subject, bodyText);
        return Task.CompletedTask;
    }
}

/// <summary>Bound from the "Smtp" section of appsettings.json. Empty Host means "not configured" - see Program.cs.</summary>
public sealed class SmtpOptions
{
    public string Host { get; set; } = "";
    public int Port { get; set; } = 587;
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    public string From { get; set; } = "AiPulse <noreply@localhost>";
    public bool EnableSsl { get; set; } = true;
}

/// <summary>Real SMTP delivery, registered instead of NullEmailSender only when Smtp:Host is set.</summary>
public sealed class SmtpEmailSender : IEmailSender
{
    private readonly SmtpOptions _options;
    private readonly ILogger<SmtpEmailSender> _log;

    public SmtpEmailSender(Microsoft.Extensions.Options.IOptions<SmtpOptions> options, ILogger<SmtpEmailSender> log)
    {
        _options = options.Value;
        _log = log;
    }

    public bool Enabled => true;

    public async Task SendAsync(string toEmail, string subject, string bodyText, CancellationToken ct = default)
    {
        using var client = new SmtpClient(_options.Host, _options.Port)
        {
            EnableSsl = _options.EnableSsl,
            Credentials = string.IsNullOrEmpty(_options.Username)
                ? null
                : new NetworkCredential(_options.Username, _options.Password)
        };
        using var message = new MailMessage(_options.From, toEmail, subject, bodyText);
        try
        {
            await client.SendMailAsync(message, ct);
        }
        catch (Exception ex)
        {
            // A failed send shouldn't break the reset flow - the admin-generated link (Users.razor) still works.
            _log.LogWarning(ex, "Failed to send email to {To} via SMTP", toEmail);
        }
    }
}
