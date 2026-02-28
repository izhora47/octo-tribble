using ldap_api.Models.Requests;
using ldap_api.Models.Responses;
using ldap_api.Services;

namespace ldap_api.Endpoints;

public static class UserEndpoints
{
    public static void MapUserEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/users").WithTags("Users");

        group.MapPost("/", CreateUser)
            .WithName("CreateUser")
            .WithSummary("Create a new AD user account")
            .WithDescription(
                "Generates a unique sAMAccountName (3+2 / 2+3 / 3+3 strategy), " +
                "generates a random password, creates the user in Active Directory, " +
                "and returns the generated credentials.");

        group.MapPut("/", UpdateUser)
            .WithName("UpdateUser")
            .WithSummary("Update AD user attributes by employeeID")
            .WithDescription(
                "Locates the user by employeeID. Only non-null fields are written. " +
                "Set UserAccountControl to 'disabled' to disable the account (accounts are never deleted).");

        group.MapGet("/{samAccountName}", GetUser)
            .WithName("GetUser")
            .WithSummary("Get AD user by sAMAccountName");
    }

    private static async Task<IResult> CreateUser(CreateUserRequest request, IAdService adService)
    {
        try
        {
            var result = await adService.CreateUserAsync(request);
            return Results.Created($"/api/users/{result.SamAccountName}",
                ApiResponse<CreateUserResponse>.Ok(result));
        }
        catch (InvalidOperationException ex)
        {
            return Results.Conflict(ApiResponse<CreateUserResponse>.Fail(ex.Message));
        }
        catch (Exception ex)
        {
            return Results.Problem(ex.Message);
        }
    }

    private static async Task<IResult> UpdateUser(UpdateUserRequest request, IAdService adService)
    {
        try
        {
            var result = await adService.UpdateUserAsync(request);
            return Results.Ok(ApiResponse<UserResponse>.Ok(result, "User updated successfully."));
        }
        catch (KeyNotFoundException ex)
        {
            return Results.NotFound(ApiResponse<UserResponse>.Fail(ex.Message));
        }
        catch (Exception ex)
        {
            return Results.Problem(ex.Message);
        }
    }

    private static async Task<IResult> GetUser(string samAccountName, IAdService adService)
    {
        try
        {
            var result = await adService.GetUserAsync(samAccountName);
            return Results.Ok(ApiResponse<UserResponse>.Ok(result));
        }
        catch (KeyNotFoundException ex)
        {
            return Results.NotFound(ApiResponse<UserResponse>.Fail(ex.Message));
        }
        catch (Exception ex)
        {
            return Results.Problem(ex.Message);
        }
    }
}
