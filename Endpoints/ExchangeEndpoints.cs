using ldap_api.Models.Requests;
using ldap_api.Models.Responses;
using ldap_api.Services;

namespace ldap_api.Endpoints;

public static class ExchangeEndpoints
{
    public static void MapExchangeEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/exchange").WithTags("Exchange");

        group.MapPost("/mailbox/enable", EnableMailbox)
            .WithName("EnableMailbox")
            .WithSummary("Enable Exchange mailbox and send onboarding email")
            .WithDescription(
                "Checks whether a mailbox already exists (Get-Mailbox), enables it if not, " +
                "then always runs Set-Mailbox (unhide from address lists) and " +
                "Set-CASMailbox (ActiveSync + OWA). The call is idempotent. " +
                "After the mailbox is confirmed enabled, the user is looked up in AD by sAMAccountName " +
                "and a welcome / onboarding email is sent to their UPN address.");

        group.MapPost("/mailbox/disable", DisableMailbox)
            .WithName("DisableMailbox")
            .WithSummary("Disable Exchange mailbox")
            .WithDescription("Runs Disable-Mailbox -Confirm:$false for the specified user.");
    }

    /// <summary>
    /// Enables the Exchange mailbox for the given sAMAccountName, then sends an onboarding
    /// email to the user's UPN (which equals their email address).
    ///
    /// Flow:
    ///   1. Connect to Exchange via WS-Man PowerShell remoting.
    ///   2. Run Get-Mailbox — if the mailbox already exists, skip Enable-Mailbox.
    ///   3. Run Enable-Mailbox (if needed), Set-Mailbox (unhide from GAL),
    ///      Set-CASMailbox (ActiveSync + OWA).
    ///   4. Verify the mailbox is now enabled (result.MailboxEnabled == true).
    ///   5. Look up the user in AD by sAMAccountName to retrieve their UserPrincipalName.
    ///   6. Send a welcome email to that UPN address (fire-and-forget; email failure
    ///      does not affect the HTTP response).
    /// </summary>
    private static async Task<IResult> EnableMailbox(
        ExchangeMailboxRequest request,
        IExchangeService exchangeService,
        IAdService adService,
        IEmailService emailService)
    {
        try
        {
            var result = await exchangeService.EnableMailboxAsync(request.SamAccountName);

            // Verify the mailbox is now enabled before sending onboarding email
            if (result.MailboxEnabled)
            {
                // Look up the user in AD to get their UPN, which is their email address
                var user = await adService.GetUserAsync(request.SamAccountName);
                if (!string.IsNullOrEmpty(user.UserPrincipalName))
                    _ = emailService.SendOnboardingEmailAsync(user.UserPrincipalName, request.SamAccountName);
            }

            return Results.Ok(ApiResponse<ExchangeMailboxResponse>.Ok(result));
        }
        catch (Exception ex)
        {
            return Results.Problem(ex.Message);
        }
    }

    private static async Task<IResult> DisableMailbox(
        ExchangeMailboxRequest request,
        IExchangeService exchangeService)
    {
        try
        {
            var result = await exchangeService.DisableMailboxAsync(request.SamAccountName);
            return Results.Ok(ApiResponse<ExchangeMailboxResponse>.Ok(result));
        }
        catch (Exception ex)
        {
            return Results.Problem(ex.Message);
        }
    }
}
