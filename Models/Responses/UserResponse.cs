namespace ldap_api.Models.Responses;

public class UserResponse
{
    public string SamAccountName { get; set; } = string.Empty;
    public string EmployeeId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string? Email { get; set; }
    public string? Office { get; set; }
    public string? Company { get; set; }
    public string? Division { get; set; }
    public string? Description { get; set; }
    public bool IsEnabled { get; set; }
    public string DistinguishedName { get; set; } = string.Empty;
}
