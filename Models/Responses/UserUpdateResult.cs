using ldap_api.Models;

namespace ldap_api.Models.Responses;

/// <summary>
/// Returned by UpdateUserAsync.
/// Contains the user's current state after the update and the list of fields that actually changed.
/// An empty Changes list means the request was processed but every submitted value
/// was identical to what was already in AD — no writes were performed.
/// </summary>
public class UserUpdateResult
{
    public UserResponse User { get; set; } = null!;
    public IReadOnlyList<ChangeRecord> Changes { get; set; } = [];
}
