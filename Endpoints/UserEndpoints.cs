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
                "Generates sAMAccountName (3+2 / 2+3 / 3+3 strategy, Cyrillic transliterated), " +
                "generates a random password, creates the account in the OU resolved from " +
                "office→OU mapping or DefaultUserOu, adds to configured groups, " +
                "sends notification email, and returns the generated credentials.");

        group.MapPut("/", UpdateUser)
            .WithName("UpdateUser")
            .WithSummary("Update AD user attributes by employeeID")
            .WithDescription(
                "Finds user by employeeID (scoped to the office OU when office is provided). " +
                "Returns 404 if the user is in 'OU=Users Disabled'. " +
                "Only non-null, non-empty fields are written. " +
                "Set UserAccountControl to 'disabled'/'enabled' to change account state. " +
                "When UpdateDisplayName=true and names change, the CN is also renamed. " +
                "Sends a notification email (with old and new values) only when at least one field actually changed.");

        group.MapGet("/by-sam/{samAccountName}", GetUser)
            .WithName("GetUserBySam")
            .WithSummary("Get AD user by sAMAccountName");

        group.MapGet("/by-employee-id/{employeeId}", GetUserByEmployeeId)
            .WithName("GetUserByEmployeeId")
            .WithSummary("Get AD user by employeeID");
    }

    private static async Task<IResult> CreateUser(
        CreateUserRequest request, IAdService adService, IEmailService emailService)
    {
        try
        {
            var result = await adService.CreateUserAsync(request);

            // Fire-and-forget is intentional: email failure must not affect the HTTP response.
            // EmailService already logs the error internally.
            _ = emailService.SendUserCreatedAsync(request, result);

            return Results.Created($"/api/users/by-sam/{result.SamAccountName}",
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

    private static async Task<IResult> UpdateUser(
        UpdateUserRequest request, IAdService adService, IEmailService emailService,
        ILogger logger)
    {
        try
        {
            var updateResult = await adService.UpdateUserAsync(request);

            if (updateResult.Changes.Count > 0)
            {
                // Send notification only when something actually changed in AD
                _ = emailService.SendUserUpdatedAsync(updateResult.User, updateResult.Changes);
            }
            else
            {
                logger.LogInformation(
                    "UpdateUser: request processed but no changes detected — email suppressed | " +
                    "employeeID={EmployeeId}", request.EmployeeId);
            }

            return Results.Ok(ApiResponse<UserResponse>.Ok(updateResult.User, "User updated successfully."));
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

    private static async Task<IResult> GetUserByEmployeeId(string employeeId, IAdService adService)
    {
        try
        {
            var result = await adService.GetUserByEmployeeIdAsync(employeeId);
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
