using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Security;
using ldap_api.Configuration;
using ldap_api.Models.Responses;
using Microsoft.Extensions.Options;

namespace ldap_api.Services;

public class ExchangeService : IExchangeService
{
    private readonly AdSettings _settings;
    private readonly ILogger<ExchangeService> _logger;

    public ExchangeService(IOptions<AdSettings> settings, ILogger<ExchangeService> logger)
    {
        _settings = settings.Value;
        _logger = logger;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Public interface
    // ─────────────────────────────────────────────────────────────────────────

    public async Task<ExchangeMailboxResponse> EnableMailboxAsync(string samAccountName, CancellationToken ct = default)
    {
        return await Task.Run(() =>
        {
            _logger.LogInformation("EnableMailbox started | sam={Sam}", samAccountName);

            using var runspace = CreateExchangeRunspace();
            _logger.LogDebug("Exchange runspace opened | sam={Sam}", samAccountName);

            _logger.LogDebug("Checking mailbox existence (Get-Mailbox) | sam={Sam}", samAccountName);
            var alreadyEnabled = MailboxExists(runspace, samAccountName);
            _logger.LogInformation("Mailbox exists: {Exists} | sam={Sam}", alreadyEnabled, samAccountName);

            if (!alreadyEnabled)
            {
                _logger.LogInformation("Running Enable-Mailbox | sam={Sam}", samAccountName);
                RunCommand(runspace, "Enable-Mailbox",
                    ps => ps.AddParameter("Identity", samAccountName),
                    samAccountName);
                _logger.LogInformation("Enable-Mailbox completed | sam={Sam}", samAccountName);
            }

            _logger.LogDebug("Running Set-Mailbox (unhide from address lists) | sam={Sam}", samAccountName);
            RunCommand(runspace, "Set-Mailbox",
                ps => ps
                    .AddParameter("Identity", samAccountName)
                    .AddParameter("HiddenFromAddressListsEnabled", false),
                samAccountName);
            _logger.LogDebug("Set-Mailbox completed | sam={Sam}", samAccountName);

            _logger.LogDebug("Running Set-CASMailbox (ActiveSync + OWA) | sam={Sam}", samAccountName);
            RunCommand(runspace, "Set-CASMailbox",
                ps => ps
                    .AddParameter("Identity", samAccountName)
                    .AddParameter("ActiveSyncEnabled", true)
                    .AddParameter("OWAforDevicesEnabled", true)
                    .AddParameter("OWAEnabled", true),
                samAccountName);
            _logger.LogDebug("Set-CASMailbox completed | sam={Sam}", samAccountName);

            _logger.LogInformation(
                "EnableMailbox completed | sam={Sam}, wasAlreadyEnabled={Already}",
                samAccountName, alreadyEnabled);

            return new ExchangeMailboxResponse
            {
                SamAccountName    = samAccountName,
                MailboxEnabled    = true,
                WasAlreadyEnabled = alreadyEnabled,
                Status = alreadyEnabled
                    ? "Mailbox was already enabled. Configuration settings applied."
                    : "Mailbox enabled and configured successfully."
            };
        }, ct);
    }

    public async Task<ExchangeMailboxResponse> DisableMailboxAsync(string samAccountName, CancellationToken ct = default)
    {
        return await Task.Run(() =>
        {
            _logger.LogInformation("DisableMailbox started | sam={Sam}", samAccountName);

            using var runspace = CreateExchangeRunspace();
            _logger.LogDebug("Exchange runspace opened | sam={Sam}", samAccountName);

            _logger.LogInformation("Running Disable-Mailbox | sam={Sam}", samAccountName);
            RunCommand(runspace, "Disable-Mailbox",
                ps => ps
                    .AddParameter("Identity", samAccountName)
                    .AddParameter("Confirm", false),
                samAccountName);

            _logger.LogInformation("DisableMailbox completed | sam={Sam}", samAccountName);

            return new ExchangeMailboxResponse
            {
                SamAccountName    = samAccountName,
                MailboxEnabled    = false,
                WasAlreadyEnabled = false,
                Status            = "Mailbox disabled successfully."
            };
        }, ct);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Helpers
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>Returns true when Get-Mailbox succeeds (mailbox exists).</summary>
    private static bool MailboxExists(Runspace runspace, string samAccountName)
    {
        using var ps = PowerShell.Create();
        ps.Runspace = runspace;
        ps.AddCommand("Get-Mailbox").AddParameter("Identity", samAccountName);
        ps.Invoke();
        return !ps.HadErrors;
    }

    private static void RunCommand(
        Runspace runspace,
        string commandName,
        Action<PowerShell> configure,
        string samAccountName)
    {
        using var ps = PowerShell.Create();
        ps.Runspace = runspace;
        ps.AddCommand(commandName);
        configure(ps);
        ps.Invoke();

        if (ps.HadErrors)
        {
            var error = ps.Streams.Error.FirstOrDefault();
            throw new InvalidOperationException(
                $"Exchange cmdlet '{commandName}' failed for '{samAccountName}': {error}");
        }
    }

    private Runspace CreateExchangeRunspace()
    {
        var uri = new Uri(_settings.ExchangePowerShellUri);
        WSManConnectionInfo connectionInfo;

        if (string.IsNullOrWhiteSpace(_settings.ServiceAccountUsername))
        {
            // Use the Windows service identity (gMSA or machine account) — no password needed
            connectionInfo = new WSManConnectionInfo(uri, "Microsoft.Exchange", PSCredential.Empty);
            connectionInfo.AuthenticationMechanism = AuthenticationMechanism.Kerberos;
        }
        else
        {
            var credential = new PSCredential(
                _settings.ServiceAccountUsername,
                ToSecureString(_settings.ServiceAccountPassword));

            connectionInfo = new WSManConnectionInfo(uri, "Microsoft.Exchange", credential);
            connectionInfo.AuthenticationMechanism = AuthenticationMechanism.Negotiate;
        }

        var runspace = RunspaceFactory.CreateRunspace(connectionInfo);
        runspace.Open();
        return runspace;
    }

    private static SecureString ToSecureString(string plain)
    {
        var secure = new SecureString();
        foreach (var c in plain)
            secure.AppendChar(c);
        secure.MakeReadOnly();
        return secure;
    }
}
