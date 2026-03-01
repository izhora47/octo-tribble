using System.Net.Mail;
using ldap_api.Configuration;
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
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_smtp.MailTo))
            return;

        var body = BuildUpdatedBody(result);
        await SendAsync("User account updated", body, _smtp.MailTo, ct);
        _logger.LogInformation(
            "Update notification sent to {To} | sam={Sam}",
            _smtp.MailTo, result.SamAccountName);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Body builders
    // ─────────────────────────────────────────────────────────────────────────

    private static string BuildCreatedBody(
        CreateUserRequest request,
        CreateUserResponse result,
        bool includePassword)
    {
        var sb = new System.Text.StringBuilder();
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

    private static string BuildUpdatedBody(UserResponse result)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("User account updated:");
        sb.AppendLine();
        sb.AppendLine($"Name     - {result.DisplayName}");
        sb.AppendLine($"Email    - {result.Email ?? "—"}");
        sb.AppendLine($"Login    - {result.SamAccountName}");
        sb.AppendLine($"ID       - {result.EmployeeId}");
        sb.AppendLine($"Office   - {result.Office ?? "—"}");
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
            // Email failures must never break the user-creation flow
            _logger.LogError(ex, "Failed to send email to {To} | subject='{Subject}'", to, subject);
        }
    }
}
