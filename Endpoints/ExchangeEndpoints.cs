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
            .WithSummary("Enable Exchange mailbox")
            .WithDescription(
                "Checks whether a mailbox already exists (Get-Mailbox), enables it if not, " +
                "then always runs Set-Mailbox (unhide from address lists) and " +
                "Set-CASMailbox (ActiveSync + OWA). The call is idempotent.");

        group.MapPost("/mailbox/disable", DisableMailbox)
            .WithName("DisableMailbox")
            .WithSummary("Disable Exchange mailbox")
            .WithDescription("Runs Disable-Mailbox -Confirm:$false for the specified user.");
    }

    private static async Task<IResult> EnableMailbox(
        ExchangeMailboxRequest request,
        IExchangeService exchangeService)
    {
        try
        {
            var result = await exchangeService.EnableMailboxAsync(request.SamAccountName);
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
