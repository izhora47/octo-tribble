namespace ldap_api.Models.Responses;

public class ExchangeMailboxResponse
{
    public string SamAccountName { get; set; } = string.Empty;
    public bool MailboxEnabled { get; set; }

    /// <summary>True when Enable was called but the mailbox already existed.</summary>
    public bool WasAlreadyEnabled { get; set; }

    public string Status { get; set; } = string.Empty;
}
