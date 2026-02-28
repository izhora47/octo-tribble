using ldap_api.Models.Responses;

namespace ldap_api.Services;

public interface IExchangeService
{
    Task<ExchangeMailboxResponse> EnableMailboxAsync(string samAccountName, CancellationToken ct = default);
    Task<ExchangeMailboxResponse> DisableMailboxAsync(string samAccountName, CancellationToken ct = default);
}
