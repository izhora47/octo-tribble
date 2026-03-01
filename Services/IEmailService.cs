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
    /// Sends a "user updated" notification to SmtpSettings.MailTo only.
    /// </summary>
    Task SendUserUpdatedAsync(
        UserResponse result,
        CancellationToken ct = default);
}
