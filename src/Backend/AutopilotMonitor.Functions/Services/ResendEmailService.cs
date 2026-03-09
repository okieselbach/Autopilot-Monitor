using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Resend;

namespace AutopilotMonitor.Functions.Services;

/// <summary>
/// Sends transactional emails via Resend.com for Private Preview notifications.
/// Best-effort: failures are logged as warnings and never propagated.
/// Temporary — remove after GA.
/// </summary>
public class ResendEmailService
{
    private readonly string _apiKey;
    private readonly ILogger<ResendEmailService> _logger;

    public ResendEmailService(
        IConfiguration configuration,
        ILogger<ResendEmailService> logger)
    {
        _logger = logger;
        _apiKey = configuration["RESEND_API_KEY"] ?? string.Empty;
    }

    /// <summary>
    /// Sends the Private Preview approval welcome email.
    /// No-op if the API key or recipient email is not configured.
    /// </summary>
    public async Task SendPreviewApprovedEmailAsync(string toEmail, string domainName)
    {
        if (string.IsNullOrWhiteSpace(_apiKey))
        {
            _logger.LogDebug("RESEND_API_KEY not configured — skipping preview approval email");
            return;
        }

        if (string.IsNullOrWhiteSpace(toEmail))
        {
            _logger.LogDebug("No notification email set — skipping preview approval email for {Domain}", domainName);
            return;
        }

        try
        {
            var resend = ResendClient.Create(_apiKey);

            var message = new EmailMessage
            {
                From = "Autopilot Monitor <noreply@autopilotmonitor.com>",
                To = toEmail,
                Subject = EmailTemplates.PreviewApprovedSubject,
                HtmlBody = EmailTemplates.GetPreviewApprovedHtml(domainName)
            };

            await resend.EmailSendAsync(message);

            _logger.LogInformation(
                "Preview approval email sent to {ToEmail} for domain {Domain}",
                toEmail, domainName);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to send preview approval email to {ToEmail} for domain {Domain}",
                toEmail, domainName);
        }
    }
}
