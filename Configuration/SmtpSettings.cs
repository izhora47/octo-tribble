namespace ldap_api.Configuration;

public class SmtpSettings
{
    /// <summary>SMTP server hostname or IP, e.g. "smtp.company.local"</summary>
    public string Server { get; set; } = string.Empty;

    /// <summary>SMTP port, typically 25 (relay) or 587 (submission).</summary>
    public int Port { get; set; } = 25;

    /// <summary>Sender address used in the From header.</summary>
    public string MailFrom { get; set; } = string.Empty;

    /// <summary>
    /// Administrative recipient (IT/HR admin).
    /// Receives all notifications and is the only recipient that gets the generated password.
    /// </summary>
    public string MailTo { get; set; } = string.Empty;

    /// <summary>
    /// Maps office name to a list of additional recipients for that office.
    /// These recipients receive the create-user notification WITHOUT the password.
    /// Example:
    ///   "NRW":    ["hr-nrw@company.com", "manager-nrw@company.com"]
    ///   "Moscow": ["hr-moscow@company.com"]
    /// </summary>
    public Dictionary<string, string[]> OfficeRecipients { get; set; } = new();
}
