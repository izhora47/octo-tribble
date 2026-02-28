using System.ComponentModel.DataAnnotations;

namespace ldap_api.Models.Requests;

/// <summary>
/// All fields except EmployeeId are optional.
/// Only non-null values are written to AD.
/// </summary>
public class UpdateUserRequest
{
    [Required]
    public string EmployeeId { get; set; } = string.Empty;

    public string? FirstName { get; set; }
    public string? LastName { get; set; }
    public string? Office { get; set; }
    public string? Company { get; set; }
    public string? Division { get; set; }
    public string? Description { get; set; }

    /// <summary>
    /// Set to "disabled" to disable the account, "enabled" to enable it.
    /// Null means no change. Use "disabled" instead of deleting — accounts are never deleted via this API.
    /// </summary>
    public string? UserAccountControl { get; set; }
}
