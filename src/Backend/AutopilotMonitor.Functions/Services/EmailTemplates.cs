namespace AutopilotMonitor.Functions.Services;

/// <summary>
/// Email templates for Resend notifications.
/// Keep all email content here for easy maintenance.
/// Temporary — remove after GA.
/// </summary>
public static class EmailTemplates
{
    public const string PreviewApprovedSubject = "Your Autopilot Monitor Private Preview is ready!";

    /// <summary>
    /// Returns the HTML body for the Private Preview approval welcome email.
    /// </summary>
    public static string GetPreviewApprovedHtml(string domainName)
    {
        var displayDomain = string.IsNullOrWhiteSpace(domainName) ? "your organization" : domainName;

        return $@"
<!DOCTYPE html>
<html>
<head><meta charset=""utf-8""></head>
<body style=""margin:0; padding:0; background-color:#f3f4f6; font-family:-apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif;"">
  <table width=""100%"" cellpadding=""0"" cellspacing=""0"" style=""background-color:#f3f4f6; padding:40px 20px;"">
    <tr><td align=""center"">
      <table width=""600"" cellpadding=""0"" cellspacing=""0"" style=""background-color:#ffffff; border-radius:12px; overflow:hidden; box-shadow:0 4px 6px rgba(0,0,0,0.07);"">

        <!-- Header -->
        <tr>
          <td style=""background:linear-gradient(135deg,#2563eb,#4f46e5); padding:32px 40px; text-align:center;"">
            <h1 style=""color:#ffffff; margin:0; font-size:24px; font-weight:700;"">Autopilot Monitor</h1>
            <p style=""color:#c7d2fe; margin:8px 0 0; font-size:14px;"">Private Preview</p>
          </td>
        </tr>

        <!-- Body -->
        <tr>
          <td style=""padding:40px;"">
            <h2 style=""color:#111827; margin:0 0 16px; font-size:20px;"">Welcome to the Private Preview!</h2>

            <p style=""color:#374151; font-size:15px; line-height:1.6; margin:0 0 16px;"">
              Great news &mdash; the Private Preview for <strong>{displayDomain}</strong> has been approved and is ready to use.
              You can now sign in and start monitoring your Windows Autopilot enrollments in real time.
            </p>

            <p style=""color:#374151; font-size:15px; line-height:1.6; margin:0 0 24px;"">
              To get started, check out the documentation for setup instructions and configuration options:
            </p>

            <!-- CTA Button -->
            <table cellpadding=""0"" cellspacing=""0"" style=""margin:0 auto 32px;"">
              <tr>
                <td style=""background-color:#2563eb; border-radius:8px;"">
                  <a href=""https://autopilotmonitor.com/docs"" target=""_blank""
                     style=""display:inline-block; padding:14px 32px; color:#ffffff; font-size:15px; font-weight:600; text-decoration:none;"">
                    View Documentation
                  </a>
                </td>
              </tr>
            </table>

            <!-- Private Preview Note -->
            <div style=""background-color:#fef3c7; border:1px solid #fde68a; border-radius:8px; padding:16px 20px; margin:0 0 24px;"">
              <p style=""color:#92400e; font-size:14px; line-height:1.5; margin:0;"">
                <strong>Please note:</strong> Autopilot Monitor is in active development. Some features are still being built
                and things may occasionally not work as expected. Your patience and understanding are greatly appreciated!
              </p>
            </div>

            <!-- Feedback -->
            <p style=""color:#374151; font-size:15px; line-height:1.6; margin:0 0 12px;"">
              Your feedback is incredibly valuable and helps shape the product. If you run into issues
              or have ideas for improvements, please don't hesitate to reach out:
            </p>

            <ul style=""color:#374151; font-size:14px; line-height:1.8; margin:0 0 24px; padding-left:20px;"">
              <li><a href=""https://github.com/okieselbach/Autopilot-Monitor/issues"" target=""_blank"" style=""color:#2563eb; text-decoration:underline;"">Open a GitHub Issue</a></li>
              <li><a href=""https://www.linkedin.com/in/oliver-kieselbach/"" target=""_blank"" style=""color:#2563eb; text-decoration:underline;"">Connect on LinkedIn</a></li>
            </ul>

            <p style=""color:#6b7280; font-size:14px; line-height:1.6; margin:0;"">
              Thanks for being an early adopter &mdash; enjoy the Private Preview!
            </p>
          </td>
        </tr>

        <!-- Footer -->
        <tr>
          <td style=""background-color:#f9fafb; padding:20px 40px; border-top:1px solid #e5e7eb; text-align:center;"">
            <p style=""color:#9ca3af; font-size:12px; margin:0;"">
              &copy; 2026 Autopilot Monitor &middot; Powered by Azure and Microsoft Identity
            </p>
          </td>
        </tr>

      </table>
    </td></tr>
  </table>
</body>
</html>";
    }
}
