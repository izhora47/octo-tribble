using System.ComponentModel.DataAnnotations;

namespace ldap_api.Models.Requests;

public class CreateUserRequest
{
    [Required]
    public string EmployeeId { get; set; } = string.Empty;

    [Required]
    public string FirstName { get; set; } = string.Empty;

    [Required]
    public string LastName { get; set; } = string.Empty;

    public string? Office { get; set; }
    public string? Company { get; set; }
    public string? Division { get; set; }
    public string? Description { get; set; }

    /// <summary>
    /// Optional OU override. DN format: "OU=Contractors,OU=Users,DC=company,DC=local"
    /// Defaults to AdSettings.DefaultUserOu when omitted.
    /// </summary>
    public string? TargetOu { get; set; }
}
