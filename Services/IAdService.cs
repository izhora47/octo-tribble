using ldap_api.Models.Requests;
using ldap_api.Models.Responses;

namespace ldap_api.Services;

public interface IAdService
{
    Task<CreateUserResponse> CreateUserAsync(CreateUserRequest request, CancellationToken ct = default);
    Task<UserResponse> UpdateUserAsync(UpdateUserRequest request, CancellationToken ct = default);
    Task<UserResponse> GetUserAsync(string samAccountName, CancellationToken ct = default);
    Task<UserResponse> GetUserByEmployeeIdAsync(string employeeId, CancellationToken ct = default);
}
