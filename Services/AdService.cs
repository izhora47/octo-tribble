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
    // Cyrillic → Latin transliteration table
    // ─────────────────────────────────────────────────────────────────────────

    private static readonly Dictionary<char, string> CyrillicMap = new()
    {
        ['А'] = "A",    ['а'] = "a",
        ['Б'] = "B",    ['б'] = "b",
        ['В'] = "V",    ['в'] = "v",
        ['Г'] = "G",    ['г'] = "g",
        ['Д'] = "D",    ['д'] = "d",
        ['Е'] = "E",    ['е'] = "e",
        ['Ё'] = "Yo",   ['ё'] = "yo",
        ['Ж'] = "Zh",   ['ж'] = "zh",
        ['З'] = "Z",    ['з'] = "z",
        ['И'] = "I",    ['и'] = "i",
        ['Й'] = "Y",    ['й'] = "y",
        ['К'] = "K",    ['к'] = "k",
        ['Л'] = "L",    ['л'] = "l",
        ['М'] = "M",    ['м'] = "m",
        ['Н'] = "N",    ['н'] = "n",
        ['О'] = "O",    ['о'] = "o",
        ['П'] = "P",    ['п'] = "p",
        ['Р'] = "R",    ['р'] = "r",
        ['С'] = "S",    ['с'] = "s",
        ['Т'] = "T",    ['т'] = "t",
        ['У'] = "U",    ['у'] = "u",
        ['Ф'] = "F",    ['ф'] = "f",
        ['Х'] = "Kh",   ['х'] = "kh",
        ['Ц'] = "Ts",   ['ц'] = "ts",
        ['Ч'] = "Ch",   ['ч'] = "ch",
        ['Ш'] = "Sh",   ['ш'] = "sh",
        ['Щ'] = "Shch", ['щ'] = "shch",
        ['Ъ'] = "",     ['ъ'] = "",
        ['Ы'] = "Y",    ['ы'] = "y",
        ['Ь'] = "",     ['ь'] = "",
        ['Э'] = "E",    ['э'] = "e",
        ['Ю'] = "Yu",   ['ю'] = "yu",
        ['Я'] = "Ya",   ['я'] = "ya",
    };

    /// <summary>Replaces Cyrillic characters with their Latin equivalents.</summary>
    private static string Transliterate(string input)
    {
        var sb = new StringBuilder(input.Length * 2);
        foreach (var c in input)
            sb.Append(CyrillicMap.TryGetValue(c, out var latin) ? latin : c.ToString());
        return sb.ToString();
    }

    /// <summary>
    /// Transliterate → strip diacritics → lowercase → ASCII letters and digits only.
    /// Used for sAMAccountName generation (no hyphens).
    /// </summary>
    private static string ToSamSafe(string name) => FilterAscii(name, allowDash: false);

    /// <summary>
    /// Transliterate → strip diacritics → lowercase → ASCII letters, digits, and hyphens.
    /// Used for email local-part generation.
    /// </summary>
    private static string ToEmailSafe(string name) => FilterAscii(name, allowDash: true);

    private static string FilterAscii(string name, bool allowDash)
    {
        var transliterated = Transliterate(name);
        var decomposed = transliterated.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                sb.Append(c);

        return new string(
            sb.ToString()
              .ToLowerInvariant()
              .Where(c => char.IsAsciiLetter(c) || char.IsAsciiDigit(c) || (allowDash && c == '-'))
              .ToArray());
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Public interface
    // ─────────────────────────────────────────────────────────────────────────

    public async Task<CreateUserResponse> CreateUserAsync(CreateUserRequest request, CancellationToken ct = default)
    {
        return await Task.Run(() =>
        {
            _logger.LogInformation(
                "CreateUser started | employeeID={EmployeeId}, name={First} {Last}",
                request.EmployeeId, request.FirstName, request.LastName);

            using var domainContext = CreateDomainContext();
            var samAccountName = ResolveSamAccountName(domainContext, request.FirstName, request.LastName);
            var password = GeneratePassword();
            var email = $"{ToEmailSafe(request.FirstName)}.{ToEmailSafe(request.LastName)}@{_settings.EmailDomain}";

            // Determine target OU: explicit override → office mapping → default
            var ouPath = ResolveOu(request.TargetOu, request.Office);
            _logger.LogInformation("Target OU resolved: {Ou}", ouPath);

            using var ouContext = CreateOuContext(ouPath);
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
            _logger.LogInformation("UserPrincipal created in AD | sam={Sam}", samAccountName);

            SetExtendedAttributes(user,
                employeeId:  request.EmployeeId,
                office:      request.Office,
                company:     request.Company,
                division:    request.Division,
                description: request.Description);

            _logger.LogInformation(
                "CreateUser completed | employeeID={EmployeeId}, sam={Sam}, email={Email}, ou={Ou}",
                request.EmployeeId, samAccountName, email, ouPath);

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
            _logger.LogInformation("UpdateUser started | employeeID={EmployeeId}", request.EmployeeId);

            using var context = CreateDomainContext();
            var user = FindByEmployeeId(context, request.EmployeeId);
            var sam = user.SamAccountName!;
            var entry = (DirectoryEntry)user.GetUnderlyingObject();

            // ── Snapshot old values for change detection and logging ─────────
            var oldGivenName   = user.GivenName;
            var oldSurname     = user.Surname;
            var oldDisplayName = user.DisplayName;
            var oldIsEnabled   = user.Enabled;
            var oldOffice      = entry.Properties["physicalDeliveryOfficeName"].Value?.ToString();
            var oldCompany     = entry.Properties["company"].Value?.ToString();
            var oldDivision    = entry.Properties["division"].Value?.ToString();
            var oldDescription = entry.Properties["description"].Value?.ToString();
            var oldManager     = entry.Properties["manager"].Value?.ToString();

            // ── Name / DisplayName ────────────────────────────────────────────
            var nameChanged = false;
            if (_settings.UpdateDisplayName)
            {
                if (request.FirstName is not null && request.FirstName != oldGivenName)
                {
                    LogChange("GivenName", oldGivenName, request.FirstName);
                    user.GivenName = request.FirstName;
                    nameChanged = true;
                }
                if (request.LastName is not null && request.LastName != oldSurname)
                {
                    LogChange("Surname", oldSurname, request.LastName);
                    user.Surname = request.LastName;
                    nameChanged = true;
                }
                if (nameChanged)
                {
                    var newDisplay = $"{user.GivenName} {user.Surname}".Trim();
                    LogChange("DisplayName", oldDisplayName, newDisplay);
                    user.DisplayName = newDisplay;
                }
            }

            // ── Account state ─────────────────────────────────────────────────
            switch (request.UserAccountControl?.ToLowerInvariant())
            {
                case "disabled" when oldIsEnabled != false:
                    LogChange("Enabled", oldIsEnabled?.ToString(), "False");
                    user.Enabled = false;
                    break;
                case "enabled" when oldIsEnabled != true:
                    LogChange("Enabled", oldIsEnabled?.ToString(), "True");
                    user.Enabled = true;
                    break;
            }

            user.Save();
            _logger.LogDebug("UserPrincipal saved | sam={Sam}", sam);

            // ── Extended attributes ───────────────────────────────────────────
            var dirty = false;

            if (request.Office is not null && request.Office != oldOffice)
            {
                LogChange("physicalDeliveryOfficeName", oldOffice, request.Office);
                SetOrClear(entry, "physicalDeliveryOfficeName", request.Office);
                dirty = true;
            }
            if (request.Company is not null && request.Company != oldCompany)
            {
                LogChange("company", oldCompany, request.Company);
                SetOrClear(entry, "company", request.Company);
                dirty = true;
            }
            if (request.Division is not null && request.Division != oldDivision)
            {
                LogChange("division", oldDivision, request.Division);
                SetOrClear(entry, "division", request.Division);
                dirty = true;
            }
            if (request.Description is not null && request.Description != oldDescription)
            {
                LogChange("description", oldDescription, request.Description);
                SetOrClear(entry, "description", request.Description);
                dirty = true;
            }

            // ── Manager lookup by employeeID ──────────────────────────────────
            if (request.ManagerEmployeeId is not null)
            {
                _logger.LogInformation(
                    "Looking up manager by employeeID: {ManagerId}", request.ManagerEmployeeId);
                var managerDn = FindDnByEmployeeId(request.ManagerEmployeeId);
                if (managerDn is not null)
                {
                    if (managerDn != oldManager)
                    {
                        LogChange("manager", oldManager, managerDn);
                        SetOrClear(entry, "manager", managerDn);
                        dirty = true;
                    }
                }
                else
                {
                    _logger.LogWarning(
                        "Manager with employeeID '{ManagerId}' not found in AD; manager attribute not updated",
                        request.ManagerEmployeeId);
                }
            }

            if (dirty)
            {
                entry.CommitChanges();
                _logger.LogDebug("Extended attributes committed | sam={Sam}", sam);
            }

            // ── CN rename (only when names actually changed) ──────────────────
            if (nameChanged && _settings.UpdateDisplayName)
            {
                var newCn = $"CN={user.GivenName} {user.Surname}".Trim();
                _logger.LogInformation(
                    "Renaming CN: '{OldName}' → '{NewCn}' | sam={Sam}", entry.Name, newCn, sam);
                entry.Rename(newCn);
                entry.CommitChanges();
                _logger.LogInformation("CN renamed successfully | sam={Sam}", sam);
            }

            // Re-fetch to return current state (updated DistinguishedName after rename)
            using var refreshed = FindBySam(context, sam);
            var response = MapToResponse(refreshed);

            _logger.LogInformation(
                "UpdateUser completed | employeeID={EmployeeId}, sam={Sam}", request.EmployeeId, sam);
            return response;
        }, ct);
    }

    public async Task<UserResponse> GetUserAsync(string samAccountName, CancellationToken ct = default)
    {
        return await Task.Run(() =>
        {
            _logger.LogInformation("GetUser | sam={Sam}", samAccountName);
            using var context = CreateDomainContext();
            var response = MapToResponse(FindBySam(context, samAccountName));
            _logger.LogInformation("GetUser completed | sam={Sam}", samAccountName);
            return response;
        }, ct);
    }

    public async Task<UserResponse> GetUserByEmployeeIdAsync(string employeeId, CancellationToken ct = default)
    {
        return await Task.Run(() =>
        {
            _logger.LogInformation("GetUserByEmployeeId | employeeID={EmployeeId}", employeeId);
            using var context = CreateDomainContext();
            var response = MapToResponse(FindByEmployeeId(context, employeeId));
            _logger.LogInformation("GetUserByEmployeeId completed | employeeID={EmployeeId}", employeeId);
            return response;
        }, ct);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // sAMAccountName generation
    //   1st: first 3 of firstName + first 2 of lastName  (johdo)
    //   2nd: first 2 of firstName + first 3 of lastName  (jodoe)
    //   3rd: first 3 of firstName + first 3 of lastName  (johdoe)
    // ─────────────────────────────────────────────────────────────────────────

    private string ResolveSamAccountName(PrincipalContext context, string firstName, string lastName)
    {
        var f = ToSamSafe(firstName);
        var l = ToSamSafe(lastName);

        var candidates = new[]
        {
            Take(f, 3) + Take(l, 2),
            Take(f, 2) + Take(l, 3),
            Take(f, 3) + Take(l, 3),
        }.Distinct();

        foreach (var candidate in candidates)
        {
            if (!SamExists(context, candidate))
            {
                _logger.LogInformation("sAMAccountName resolved: '{Sam}'", candidate);
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

    private static string Take(string s, int n) => s.Length >= n ? s[..n] : s;

    // ─────────────────────────────────────────────────────────────────────────
    // Password generation
    // ─────────────────────────────────────────────────────────────────────────

    private static string GeneratePassword(int length = 12)
    {
        const string upper   = "ABCDEFGHJKLMNPQRSTUVWXYZ";
        const string lower   = "abcdefghjkmnpqrstuvwxyz";
        const string digits  = "23456789";
        const string special = "!@#$%*";
        const string all     = upper + lower + digits + special;

        var bytes = new byte[length * 2];
        RandomNumberGenerator.Fill(bytes);

        var pwd = new char[length];
        pwd[0] = upper  [bytes[0] % upper.Length];
        pwd[1] = lower  [bytes[1] % lower.Length];
        pwd[2] = digits [bytes[2] % digits.Length];
        pwd[3] = special[bytes[3] % special.Length];

        for (var i = 4; i < length; i++)
            pwd[i] = all[bytes[i] % all.Length];

        for (var i = length - 1; i > 0; i--)
        {
            var j = bytes[length + i] % (i + 1);
            (pwd[i], pwd[j]) = (pwd[j], pwd[i]);
        }

        return new string(pwd);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // OU resolution
    // ─────────────────────────────────────────────────────────────────────────

    private string ResolveOu(string? targetOuOverride, string? office)
    {
        if (targetOuOverride is not null)
            return targetOuOverride;

        if (office is not null && _settings.OfficeOuMappings.TryGetValue(office, out var mappedOu))
        {
            _logger.LogDebug("OU mapped: office='{Office}' → '{Ou}'", office, mappedOu);
            return mappedOu;
        }

        return _settings.DefaultUserOu;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // AD context / DirectoryEntry helpers
    // ─────────────────────────────────────────────────────────────────────────

    private PrincipalContext CreateOuContext(string ouPath) =>
        HasServiceAccount()
            ? new PrincipalContext(ContextType.Domain, _settings.Domain, ouPath,
                _settings.ServiceAccountUsername, _settings.ServiceAccountPassword)
            : new PrincipalContext(ContextType.Domain, _settings.Domain, ouPath);

    private PrincipalContext CreateDomainContext() =>
        HasServiceAccount()
            ? new PrincipalContext(ContextType.Domain, _settings.Domain,
                _settings.ServiceAccountUsername, _settings.ServiceAccountPassword)
            : new PrincipalContext(ContextType.Domain, _settings.Domain);

    private DirectoryEntry CreateDirectoryEntry() =>
        HasServiceAccount()
            ? new DirectoryEntry($"LDAP://{_settings.Domain}",
                _settings.ServiceAccountUsername, _settings.ServiceAccountPassword)
            : new DirectoryEntry($"LDAP://{_settings.Domain}");

    private bool HasServiceAccount() =>
        !string.IsNullOrWhiteSpace(_settings.ServiceAccountUsername);

    private UserPrincipal FindByEmployeeId(PrincipalContext context, string employeeId)
    {
        using var root     = CreateDirectoryEntry();
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

    /// <summary>Returns the DN of a user by employeeID, or null if not found.</summary>
    private string? FindDnByEmployeeId(string employeeId)
    {
        using var root     = CreateDirectoryEntry();
        using var searcher = new DirectorySearcher(root)
        {
            Filter = $"(&(objectClass=user)(objectCategory=person)(employeeID={employeeId}))"
        };
        searcher.PropertiesToLoad.Add("distinguishedName");

        return searcher.FindOne()?.Properties["distinguishedName"][0]?.ToString();
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

    private void LogChange(string field, string? oldValue, string? newValue) =>
        _logger.LogInformation(
            "  [{Field}] '{Old}' → '{New}'", field,
            oldValue ?? "(empty)", newValue ?? "(empty)");

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
