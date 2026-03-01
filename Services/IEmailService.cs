using ldap_api.Models;
using ldap_api.Models.Requests;
using ldap_api.Models.Responses;

namespace ldap_api.Services;

public interface IEmailService
{
    /// <summary>
    /// Sends a "user created" notification.
    /// Office recipients (from SmtpSettings.OfficeRecipients) receive the body without the password.
    /// The admin address (SmtpSettings.MailTo) receives the same body including the password.
    /// </summary>
    Task SendUserCreatedAsync(
        CreateUserRequest request,
        CreateUserResponse result,
        CancellationToken ct = default);

    /// <summary>
    /// Sends a "user updated" notification to SmtpSettings.MailTo.
    /// Each changed field is listed with its old and new value.
    /// Only called when at least one field actually changed.
    /// </summary>
    Task SendUserUpdatedAsync(
        UserResponse result,
        IReadOnlyList<ChangeRecord> changes,
        CancellationToken ct = default);

    /// <summary>
    /// Sends a welcome / onboarding email directly to the new user.
    /// Called after the Exchange mailbox has been successfully enabled.
    /// </summary>
    Task SendOnboardingEmailAsync(
        string toEmail,
        string samAccountName,
        CancellationToken ct = default);
}
