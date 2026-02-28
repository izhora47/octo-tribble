using System.DirectoryServices;
using System.DirectoryServices.AccountManagement;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using ldap_api.Configuration;
using ldap_api.Models.Requests;
using ldap_api.Models.Responses;
using Microsoft.Extensions.Options;

namespace ldap_api.Services;

public class AdService : IAdService
{
    private readonly AdSettings _settings;
    private readonly ILogger<AdService> _logger;

    public AdService(IOptions<AdSettings> settings, ILogger<AdService> logger)
    {
        _settings = settings.Value;
        _logger = logger;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Public interface
    // ─────────────────────────────────────────────────────────────────────────

    public async Task<CreateUserResponse> CreateUserAsync(CreateUserRequest request, CancellationToken ct = default)
    {
        return await Task.Run(() =>
        {
            // Resolve a unique sAMAccountName before opening the creation context
            using var domainContext = CreateDomainContext();
            var samAccountName = ResolveSamAccountName(domainContext, request.FirstName, request.LastName);
            var password = GeneratePassword();
            var email = $"{request.FirstName.ToLowerInvariant()}.{request.LastName.ToLowerInvariant()}@{_settings.EmailDomain}";

            using var ouContext = CreateOuContext(request.TargetOu);
            var user = new UserPrincipal(ouContext)
            {
                GivenName         = request.FirstName,
                Surname           = request.LastName,
                DisplayName       = $"{request.FirstName} {request.LastName}",
                SamAccountName    = samAccountName,
                UserPrincipalName = $"{samAccountName}@{_settings.Domain}",
                EmailAddress      = email,
                Enabled           = true
            };

            user.SetPassword(password);
            user.Save();

            SetExtendedAttributes(user,
                employeeId:  request.EmployeeId,
                office:      request.Office,
                company:     request.Company,
                division:    request.Division,
                description: request.Description);

            _logger.LogInformation(
                "Created AD user {SamAccountName} (employeeID: {EmployeeId})",
                samAccountName, request.EmployeeId);

            return new CreateUserResponse
            {
                Status         = "created",
                EmployeeId     = request.EmployeeId,
                SamAccountName = samAccountName,
                Email          = email,
                Password       = password
            };
        }, ct);
    }

    public async Task<UserResponse> UpdateUserAsync(UpdateUserRequest request, CancellationToken ct = default)
    {
        return await Task.Run(() =>
        {
            using var context = CreateDomainContext();
            var user = FindByEmployeeId(context, request.EmployeeId);

            if (_settings.UpdateDisplayName)
            {
                if (request.FirstName is not null) user.GivenName = request.FirstName;
                if (request.LastName  is not null) user.Surname   = request.LastName;

                if (request.FirstName is not null || request.LastName is not null)
                    user.DisplayName = $"{user.GivenName} {user.Surname}".Trim();
            }

            switch (request.UserAccountControl?.ToLowerInvariant())
            {
                case "disabled": user.Enabled = false; break;
                case "enabled":  user.Enabled = true;  break;
            }

            user.Save();

            SetExtendedAttributes(user,
                office:      request.Office,
                company:     request.Company,
                division:    request.Division,
                description: request.Description);

            _logger.LogInformation("Updated AD user (employeeID: {EmployeeId})", request.EmployeeId);
            return MapToResponse(user);
        }, ct);
    }

    public async Task<UserResponse> GetUserAsync(string samAccountName, CancellationToken ct = default)
    {
        return await Task.Run(() =>
        {
            using var context = CreateDomainContext();
            return MapToResponse(FindBySam(context, samAccountName));
        }, ct);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // sAMAccountName generation
    //   1st attempt: first 3 chars of firstName + first 2 chars of lastName  (3+2)
    //   2nd attempt: first 2 chars of firstName + first 3 chars of lastName  (2+3)
    //   3rd attempt: first 3 chars of firstName + first 3 chars of lastName  (3+3)
    // ─────────────────────────────────────────────────────────────────────────

    private string ResolveSamAccountName(PrincipalContext context, string firstName, string lastName)
    {
        var f = Sanitize(firstName);
        var l = Sanitize(lastName);

        var candidates = new[]
        {
            Take(f, 3) + Take(l, 2),   // johdo
            Take(f, 2) + Take(l, 3),   // jodoe
            Take(f, 3) + Take(l, 3),   // johdoe
        }.Distinct();

        foreach (var candidate in candidates)
        {
            if (!SamExists(context, candidate))
            {
                _logger.LogDebug("Resolved sAMAccountName '{Sam}' for {First} {Last}",
                    candidate, firstName, lastName);
                return candidate;
            }
            _logger.LogDebug("sAMAccountName '{Sam}' already taken, trying next candidate", candidate);
        }

        throw new InvalidOperationException(
            $"All generated sAMAccountName candidates for '{firstName} {lastName}' are already taken. " +
            "Please create the account manually.");
    }

    private static bool SamExists(PrincipalContext context, string sam) =>
        UserPrincipal.FindByIdentity(context, IdentityType.SamAccountName, sam) is not null;

    /// <summary>Strips diacritics, removes non-ASCII letters/digits, lowercases.</summary>
    private static string Sanitize(string name)
    {
        var decomposed = name.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                sb.Append(c);

        return new string(
            sb.ToString()
              .ToLowerInvariant()
              .Where(c => char.IsAsciiLetter(c) || char.IsAsciiDigit(c))
              .ToArray());
    }

    private static string Take(string s, int n) => s.Length >= n ? s[..n] : s;

    // ─────────────────────────────────────────────────────────────────────────
    // Password generation
    // ─────────────────────────────────────────────────────────────────────────

    private static string GeneratePassword(int length = 12)
    {
        // Visually ambiguous characters excluded to ease copy-paste (I/O/0/1/l)
        const string upper   = "ABCDEFGHJKLMNPQRSTUVWXYZ";
        const string lower   = "abcdefghjkmnpqrstuvwxyz";
        const string digits  = "23456789";
        const string special = "!@#$%*";
        const string all     = upper + lower + digits + special;

        var bytes = new byte[length * 2]; // second half used for Fisher-Yates shuffle
        RandomNumberGenerator.Fill(bytes);

        var pwd = new char[length];

        // Guarantee at least one character from each required complexity class
        pwd[0] = upper  [bytes[0] % upper.Length];
        pwd[1] = lower  [bytes[1] % lower.Length];
        pwd[2] = digits [bytes[2] % digits.Length];
        pwd[3] = special[bytes[3] % special.Length];

        for (var i = 4; i < length; i++)
            pwd[i] = all[bytes[i] % all.Length];

        // Fisher-Yates shuffle so the guaranteed chars aren't always in positions 0-3
        for (var i = length - 1; i > 0; i--)
        {
            var j = bytes[length + i] % (i + 1);
            (pwd[i], pwd[j]) = (pwd[j], pwd[i]);
        }

        return new string(pwd);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // AD context / DirectoryEntry helpers
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>Context scoped to a specific OU — used when creating new accounts.</summary>
    private PrincipalContext CreateOuContext(string? ouOverride = null)
    {
        var container = ouOverride ?? _settings.DefaultUserOu;
        return HasServiceAccount()
            ? new PrincipalContext(ContextType.Domain, _settings.Domain, container,
                _settings.ServiceAccountUsername, _settings.ServiceAccountPassword)
            : new PrincipalContext(ContextType.Domain, _settings.Domain, container);
    }

    /// <summary>Domain-wide context — used for lookups that must span all OUs.</summary>
    private PrincipalContext CreateDomainContext()
    {
        return HasServiceAccount()
            ? new PrincipalContext(ContextType.Domain, _settings.Domain,
                _settings.ServiceAccountUsername, _settings.ServiceAccountPassword)
            : new PrincipalContext(ContextType.Domain, _settings.Domain);
    }

    private DirectoryEntry CreateDirectoryEntry() =>
        HasServiceAccount()
            ? new DirectoryEntry($"LDAP://{_settings.Domain}",
                _settings.ServiceAccountUsername, _settings.ServiceAccountPassword)
            : new DirectoryEntry($"LDAP://{_settings.Domain}");

    private bool HasServiceAccount() =>
        !string.IsNullOrWhiteSpace(_settings.ServiceAccountUsername);

    /// <summary>Finds a user by the employeeID LDAP attribute using DirectorySearcher.</summary>
    private UserPrincipal FindByEmployeeId(PrincipalContext context, string employeeId)
    {
        using var root    = CreateDirectoryEntry();
        using var searcher = new DirectorySearcher(root)
        {
            Filter = $"(&(objectClass=user)(objectCategory=person)(employeeID={employeeId}))"
        };
        searcher.PropertiesToLoad.Add("distinguishedName");

        var result = searcher.FindOne()
            ?? throw new KeyNotFoundException(
                $"No user with employeeID '{employeeId}' found in Active Directory.");

        var dn = result.Properties["distinguishedName"][0]!.ToString()!;

        return UserPrincipal.FindByIdentity(context, IdentityType.DistinguishedName, dn)
            ?? throw new KeyNotFoundException(
                $"User with employeeID '{employeeId}' was located in LDAP but could not be loaded as UserPrincipal.");
    }

    private static UserPrincipal FindBySam(PrincipalContext context, string sam) =>
        UserPrincipal.FindByIdentity(context, IdentityType.SamAccountName, sam)
            ?? throw new KeyNotFoundException($"User '{sam}' was not found in Active Directory.");

    // ─────────────────────────────────────────────────────────────────────────
    // Extended attribute helpers
    // ─────────────────────────────────────────────────────────────────────────

    private static void SetExtendedAttributes(
        UserPrincipal user,
        string? employeeId  = null,
        string? office      = null,
        string? company     = null,
        string? division    = null,
        string? description = null)
    {
        var entry = (DirectoryEntry)user.GetUnderlyingObject();
        var dirty = false;

        if (employeeId  is not null) { SetOrClear(entry, "employeeID",                 employeeId);  dirty = true; }
        if (office      is not null) { SetOrClear(entry, "physicalDeliveryOfficeName", office);      dirty = true; }
        if (company     is not null) { SetOrClear(entry, "company",                    company);     dirty = true; }
        if (division    is not null) { SetOrClear(entry, "division",                   division);    dirty = true; }
        if (description is not null) { SetOrClear(entry, "description",                description); dirty = true; }

        if (dirty) entry.CommitChanges();
    }

    private static void SetOrClear(DirectoryEntry entry, string attribute, string value)
    {
        if (string.IsNullOrEmpty(value))
            entry.Properties[attribute].Clear();
        else
            entry.Properties[attribute].Value = value;
    }

    private static UserResponse MapToResponse(UserPrincipal user)
    {
        var entry = (DirectoryEntry)user.GetUnderlyingObject();
        return new UserResponse
        {
            SamAccountName    = user.SamAccountName ?? string.Empty,
            EmployeeId        = entry.Properties["employeeID"].Value?.ToString() ?? string.Empty,
            DisplayName       = user.DisplayName ?? string.Empty,
            Email             = user.EmailAddress,
            Office            = entry.Properties["physicalDeliveryOfficeName"].Value?.ToString(),
            Company           = entry.Properties["company"].Value?.ToString(),
            Division          = entry.Properties["division"].Value?.ToString(),
            Description       = entry.Properties["description"].Value?.ToString(),
            IsEnabled         = user.Enabled ?? false,
            DistinguishedName = user.DistinguishedName ?? string.Empty
        };
    }
}
