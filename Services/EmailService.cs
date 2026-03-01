using System.Net.Mail;
using System.Text;
using ldap_api.Configuration;
using ldap_api.Models;
using ldap_api.Models.Requests;
using ldap_api.Models.Responses;
using Microsoft.Extensions.Options;

namespace ldap_api.Services;

public class EmailService : IEmailService
{
    private readonly SmtpSettings _smtp;
    private readonly ILogger<EmailService> _logger;

    public EmailService(IOptions<SmtpSettings> smtp, ILogger<EmailService> logger)
    {
        _smtp = smtp.Value;
        _logger = logger;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Public interface
    // ─────────────────────────────────────────────────────────────────────────

    public async Task SendUserCreatedAsync(
        CreateUserRequest request,
        CreateUserResponse result,
        CancellationToken ct = default)
    {
        if (!_smtp.SendEmailNotifications)
        {
            _logger.LogDebug("Email notifications disabled — skipping create notification | sam={Sam}", result.SamAccountName);
            return;
        }

        var baseBody = BuildCreatedBody(request, result, includePassword: false);
        var adminBody = BuildCreatedBody(request, result, includePassword: true);

        // Office recipients — body without password
        if (request.Office is not null &&
            _smtp.OfficeRecipients.TryGetValue(request.Office, out var officeEmails))
        {
            foreach (var to in officeEmails)
            {
                await SendAsync("New user account created", baseBody, to, ct);
                _logger.LogInformation(
                    "Create notification sent to office recipient {To} | sam={Sam}",
                    to, result.SamAccountName);
            }
        }

        // Admin recipient — body with password
        if (!string.IsNullOrWhiteSpace(_smtp.MailTo))
        {
            await SendAsync("New user account created", adminBody, _smtp.MailTo, ct);
            _logger.LogInformation(
                "Create notification sent to admin {To} | sam={Sam}",
                _smtp.MailTo, result.SamAccountName);
        }
    }

    public async Task SendUserUpdatedAsync(
        UserResponse result,
        IReadOnlyList<ChangeRecord> changes,
        CancellationToken ct = default)
    {
        if (!_smtp.SendEmailNotifications)
        {
            _logger.LogDebug("Email notifications disabled — skipping update notification | sam={Sam}", result.SamAccountName);
            return;
        }

        if (string.IsNullOrWhiteSpace(_smtp.MailTo))
            return;

        var body = BuildUpdatedBody(result, changes);
        await SendAsync("User account updated", body, _smtp.MailTo, ct);
        _logger.LogInformation(
            "Update notification sent to {To} | sam={Sam}",
            _smtp.MailTo, result.SamAccountName);
    }

    public async Task SendOnboardingEmailAsync(
        string toEmail,
        string samAccountName,
        CancellationToken ct = default)
    {
        if (!_smtp.SendEmailNotifications)
        {
            _logger.LogDebug("Email notifications disabled — skipping onboarding email | sam={Sam}", samAccountName);
            return;
        }

        const string subject = "Welcome";
        var body = $"Your San is {samAccountName}. Welcome to our company";
        await SendAsync(subject, body, toEmail, ct);
        _logger.LogInformation(
            "Onboarding email sent to {To} | sam={Sam}", toEmail, samAccountName);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Body builders
    // ─────────────────────────────────────────────────────────────────────────

    private static string BuildCreatedBody(
        CreateUserRequest request,
        CreateUserResponse result,
        bool includePassword)
    {
        var sb = new StringBuilder();
        sb.AppendLine("New user account created:");
        sb.AppendLine();
        sb.AppendLine($"Name     - {request.FirstName}");
        sb.AppendLine($"LastName - {request.LastName}");
        sb.AppendLine($"Email    - {result.Email}");
        sb.AppendLine($"Login    - {result.SamAccountName}");
        sb.AppendLine($"ID       - {result.EmployeeId}");
        sb.AppendLine($"Office   - {request.Office ?? "—"}");
        if (includePassword)
        {
            sb.AppendLine();
            sb.AppendLine($"Password - {result.Password}");
        }
        return sb.ToString();
    }

    private static string BuildUpdatedBody(UserResponse result, IReadOnlyList<ChangeRecord> changes)
    {
        var sb = new StringBuilder();
        sb.AppendLine("User account updated:");
        sb.AppendLine();
        sb.AppendLine($"Login    - {result.SamAccountName}");
        sb.AppendLine($"ID       - {result.EmployeeId}");
        sb.AppendLine();
        sb.AppendLine("Changes:");
        foreach (var c in changes)
        {
            sb.AppendLine($"[{c.Field}]");
            sb.AppendLine($"  Old value: {c.OldValue ?? "(empty)"}");
            sb.AppendLine($"  New value: {c.NewValue ?? "(empty)"}");
        }
        return sb.ToString();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // SMTP dispatch
    // ─────────────────────────────────────────────────────────────────────────

    private async Task SendAsync(string subject, string body, string to, CancellationToken ct)
    {
        try
        {
            using var client = new SmtpClient(_smtp.Server, _smtp.Port);
            using var message = new MailMessage(_smtp.MailFrom, to, subject, body);
            await client.SendMailAsync(message, ct);
        }
        catch (Exception ex)
        {
            // Email failures must never break the user-creation or update flow
            _logger.LogError(ex, "Failed to send email to {To} | subject='{Subject}'", to, subject);
        }
    }
}
