namespace ldap_api.Models.Responses;

public class CreateUserResponse
{
    /// <summary>"created" or an error description</summary>
    public string Status { get; set; } = string.Empty;

    public string EmployeeId { get; set; } = string.Empty;
    public string SamAccountName { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;

    /// <summary>The auto-generated initial password. Store or forward securely — it is not persisted.</summary>
    public string Password { get; set; } = string.Empty;
}
