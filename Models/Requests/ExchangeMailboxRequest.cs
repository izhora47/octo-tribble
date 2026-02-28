using System.ComponentModel.DataAnnotations;

namespace ldap_api.Models.Requests;

public class ExchangeMailboxRequest
{
    [Required]
    public string SamAccountName { get; set; } = string.Empty;
}
