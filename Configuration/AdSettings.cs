namespace ldap_api.Configuration;

public class AdSettings
{
    /// <summary>AD domain name, e.g. "company.local"</summary>
    public string Domain { get; set; } = string.Empty;

    /// <summary>
    /// Domain used when building the user's email address: {samAccountName}@EmailDomain
    /// Often differs from the internal AD domain (e.g. AD = "company.local", email = "company.com")
    /// </summary>
    public string EmailDomain { get; set; } = string.Empty;

    /// <summary>Default OU where new users are created. DN format: "OU=Users,DC=company,DC=local"</summary>
    public string DefaultUserOu { get; set; } = string.Empty;

    /// <summary>
    /// Credentials used to bind to Active Directory (PrincipalContext / DirectoryEntry)
    /// and to authenticate against the Exchange PowerShell remoting endpoint.
    ///
    /// These are NOT the Windows service logon account — that is configured separately
    /// via Services Manager or sc.exe when installing the service.
    ///
    /// Leave both empty to run under the Windows service identity (recommended when the
    /// service account already holds the required AD and Exchange permissions).
    /// </summary>
    public string ServiceAccountUsername { get; set; } = string.Empty;
    public string ServiceAccountPassword { get; set; } = string.Empty;

    /// <summary>Exchange PowerShell remoting endpoint, e.g. "http://exchange-server.company.local/PowerShell"</summary>
    public string ExchangePowerShellUri { get; set; } = string.Empty;

    /// <summary>
    /// When true, UpdateUser will overwrite GivenName / Surname / DisplayName in AD.
    /// Set to false if names should be treated as immutable after account creation.
    /// </summary>
    public bool UpdateDisplayName { get; set; } = true;
}
