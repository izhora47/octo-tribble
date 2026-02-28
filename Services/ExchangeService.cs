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

    /// <summary>
    /// Full mailbox enable flow:
    ///   1. Get-Mailbox   — check if a mailbox already exists
    ///   2. Enable-Mailbox — create the mailbox (skipped if already exists)
    ///   3. Set-Mailbox   — unhide from address lists
    ///   4. Set-CASMailbox — enable ActiveSync + OWA
    /// The configure steps always run, making the call idempotent.
    /// </summary>
    public async Task<ExchangeMailboxResponse> EnableMailboxAsync(string samAccountName, CancellationToken ct = default)
    {
        return await Task.Run(() =>
        {
            using var runspace = CreateExchangeRunspace();

            var alreadyEnabled = MailboxExists(runspace, samAccountName);

            if (!alreadyEnabled)
            {
                RunCommand(runspace, "Enable-Mailbox",
                    ps => ps.AddParameter("Identity", samAccountName),
                    samAccountName);
            }

            // Configure regardless — ensures idempotency on repeat calls
            RunCommand(runspace, "Set-Mailbox",
                ps => ps
                    .AddParameter("Identity", samAccountName)
                    .AddParameter("HiddenFromAddressListsEnabled", false),
                samAccountName);

            RunCommand(runspace, "Set-CASMailbox",
                ps => ps
                    .AddParameter("Identity", samAccountName)
                    .AddParameter("ActiveSyncEnabled", true)
                    .AddParameter("OWAforDevicesEnabled", true)
                    .AddParameter("OWAEnabled", true),
                samAccountName);

            _logger.LogInformation(
                "Mailbox for {SamAccountName} enabled (wasAlreadyEnabled={Already})",
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
            using var runspace = CreateExchangeRunspace();

            RunCommand(runspace, "Disable-Mailbox",
                ps => ps
                    .AddParameter("Identity", samAccountName)
                    .AddParameter("Confirm", false),
                samAccountName);

            _logger.LogInformation("Mailbox for {SamAccountName} disabled", samAccountName);

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
            // Use the Windows service identity (Kerberos / machine account)
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
