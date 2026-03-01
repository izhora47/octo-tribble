namespace ldap_api.Models;

/// <summary>Records a single field change made during an update operation.</summary>
public record ChangeRecord(string Field, string? OldValue, string? NewValue);
