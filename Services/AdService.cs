using System.Collections.Concurrent;
using System.DirectoryServices;
using System.DirectoryServices.AccountManagement;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using ldap_api.Configuration;
using ldap_api.Models;
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
        ['А'] = "A",
        ['а'] = "a",
        ['Б'] = "B",
        ['б'] = "b",
        ['В'] = "V",
        ['в'] = "v",
        ['Г'] = "G",
        ['г'] = "g",
        ['Д'] = "D",
        ['д'] = "d",
        ['Е'] = "E",
        ['е'] = "e",
        ['Ё'] = "E",
        ['ё'] = "e",
        ['Ж'] = "Zh",
        ['ж'] = "zh",
        ['З'] = "Z",
        ['з'] = "z",
        ['И'] = "I",
        ['и'] = "i",
        ['Й'] = "Iy",
        ['й'] = "y",
        ['К'] = "K",
        ['к'] = "k",
        ['Л'] = "L",
        ['л'] = "l",
        ['М'] = "M",
        ['м'] = "m",
        ['Н'] = "N",
        ['н'] = "n",
        ['О'] = "O",
        ['о'] = "o",
        ['П'] = "P",
        ['п'] = "p",
        ['Р'] = "R",
        ['р'] = "r",
        ['С'] = "S",
        ['с'] = "s",
        ['Т'] = "T",
        ['т'] = "t",
        ['У'] = "U",
        ['у'] = "u",
        ['Ф'] = "F",
        ['ф'] = "f",
        ['Х'] = "Kh",
        ['х'] = "kh",
        ['Ц'] = "Ts",
        ['ц'] = "ts",
        ['Ч'] = "Ch",
        ['ч'] = "ch",
        ['Ш'] = "Sh",
        ['ш'] = "sh",
        ['Щ'] = "Sch",
        ['щ'] = "sch",
        ['Ъ'] = "",
        ['ъ'] = "",
        ['Ы'] = "Y",
        ['ы'] = "y",
        ['Ь'] = "",
        ['ь'] = "",
        ['Э'] = "E",
        ['э'] = "e",
        ['Ю'] = "Yu",
        ['ю'] = "yu",
        ['Я'] = "Ya",
        ['я'] = "ya",
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

    /// <summary>
    /// Transliterate → strip diacritics → ASCII letters, digits, spaces, and hyphens.
    /// Preserves original casing. Used for GivenName, Surname, DisplayName, and CN.
    /// </summary>
    private static string ToLatinName(string name)
    {
        var transliterated = Transliterate(name);
        var decomposed = transliterated.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                sb.Append(c);
        return new string(
            sb.ToString()
              .Where(c => char.IsAsciiLetter(c) || char.IsAsciiDigit(c) || c == '-' || c == ' ')
              .ToArray());
    }

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

            // Guard: reject if this employeeID is already in use
            var existingDn = FindDnByEmployeeId(request.EmployeeId);
            if (existingDn is not null)
                throw new InvalidOperationException(
                    $"A user with employeeID '{request.EmployeeId}' already exists in Active Directory.");

            var (samAccountName, algoIndex) = ResolveSamAccountName(domainContext, request.FirstName, request.LastName);
            var password = GeneratePassword();

            // Suffix tracks which SAM algorithm was used; applied to CN, email, and UPN
            // so all three stay unique when the same first+last name already exists:
            //   algorithm 0 → no suffix     CN=John Doe          john.doe@…
            //   algorithm 1 → suffix "1"    CN=John Doe1         john.doe1@…
            //   algorithm 2 → suffix "2"    CN=John Doe2         john.doe2@…
            var suffix = algoIndex == 0 ? "" : algoIndex.ToString();
            var localPart = $"{ToEmailSafe(request.FirstName)}.{ToEmailSafe(request.LastName)}{suffix}";
            var email = $"{localPart}@{_settings.EmailDomain}";
            var upn = $"{localPart}@{_settings.EmailDomain}";
            var latinFirst = ToLatinName(request.FirstName);
            var latinLast = ToLatinName(request.LastName);
            var cn = $"{latinFirst} {latinLast}{suffix}";

            // Determine target OU: explicit override → office mapping → default
            var ouPath = ResolveOu(request.TargetOu, request.Office);
            _logger.LogInformation("Target OU resolved: {Ou}", ouPath);

            using var ouContext = CreateOuContext(ouPath);
            var user = new UserPrincipal(ouContext)
            {
                Name = cn,
                GivenName = latinFirst,
                Surname = latinLast,
                DisplayName = $"{latinFirst} {latinLast}",
                SamAccountName = samAccountName,
                UserPrincipalName = upn,
                EmailAddress = email,
                // Default is enabled; "disabled" in the request creates the account disabled
                Enabled = !string.Equals(request.UserAccountControl, "disabled", StringComparison.OrdinalIgnoreCase)
            };

            user.SetPassword(password);
            user.Save();
            _logger.LogInformation("UserPrincipal created in AD | sam={Sam}", samAccountName);

            SetExtendedAttributes(user,
                employeeId: request.EmployeeId,
                office: request.Office,
                company: request.Company,
                division: request.Division,
                description: request.Description);

            // Set creation-only attributes that require DirectoryEntry access
            var newEntry = (DirectoryEntry)user.GetUnderlyingObject();
            // Force "must change password at next logon"
            newEntry.Properties["pwdLastSet"].Value = 0;
            // extensionAttribute15=1 is monitored by Windows Scheduler to provision the SfB (Skype for Business) account
            if (SchemaHasAttribute("extensionAttribute15"))
                newEntry.Properties["extensionAttribute15"].Value = "1";
            else
                _logger.LogWarning(
                    "Attribute 'extensionAttribute15' not found in AD schema " +
                    "(Exchange schema extension may not be installed) — skipping | sam={Sam}", samAccountName);
            newEntry.CommitChanges();

            AddUserToGroups(user, request.Office);

            _logger.LogInformation(
                "CreateUser completed | employeeID={EmployeeId}, sam={Sam}, email={Email}, ou={Ou}",
                request.EmployeeId, samAccountName, email, ouPath);

            return new CreateUserResponse
            {
                Status = "created",
                EmployeeId = request.EmployeeId,
                SamAccountName = samAccountName,
                Email = email,
                Password = password
            };
        }, ct);
    }

    public async Task<UserUpdateResult> UpdateUserAsync(UpdateUserRequest request, CancellationToken ct = default)
    {
        return await Task.Run(() =>
        {
            _logger.LogInformation("UpdateUser started | employeeID={EmployeeId}", request.EmployeeId);

            using var context = CreateDomainContext();

            // Scope search to the OU that matches the user's office (when provided and mapped),
            // avoiding a domain-wide scan.  Falls back to domain-wide if office is unknown.
            string? searchOuDn = null;
            if (request.Office is not null)
                _settings.OfficeOuMappings.TryGetValue(request.Office, out searchOuDn);

            var user = FindByEmployeeId(context, request.EmployeeId, searchOuDn);
            var sam = user.SamAccountName!;
            var entry = (DirectoryEntry)user.GetUnderlyingObject();

            // Guard: refuse to update accounts that have been moved to the disabled-users OU
            if (user.DistinguishedName?.Contains("OU=Users Disabled", StringComparison.OrdinalIgnoreCase) == true)
                throw new KeyNotFoundException(
                    $"User with employeeID '{request.EmployeeId}' is in 'OU=Users Disabled' and cannot be updated.");

            // ── Snapshot old values for change detection ───────────────────────
            var oldGivenName = user.GivenName;
            var oldSurname = user.Surname;
            var oldDisplayName = user.DisplayName;
            var oldIsEnabled = user.Enabled;
            var oldOffice = entry.Properties["physicalDeliveryOfficeName"].Value?.ToString();
            var oldCompany = entry.Properties["company"].Value?.ToString();
            var oldDivision = entry.Properties["division"].Value?.ToString();
            var oldDescription = entry.Properties["description"].Value?.ToString();
            var oldManager = entry.Properties["manager"].Value?.ToString();

            // Local helper: record + log a field change
            var changes = new List<ChangeRecord>();
            void Track(string field, string? oldV, string? newV)
            {
                changes.Add(new ChangeRecord(field, oldV, newV));
                LogChange(field, oldV, newV);
            }

            // ── Name / DisplayName ────────────────────────────────────────────
            var nameChanged = false;
            if (_settings.UpdateDisplayName)
            {
                if (!string.IsNullOrEmpty(request.FirstName))
                {
                    var latinFirst = ToLatinName(request.FirstName);
                    if (latinFirst != oldGivenName)
                    {
                        Track("GivenName", oldGivenName, latinFirst);
                        user.GivenName = latinFirst;
                        nameChanged = true;
                    }
                }
                if (!string.IsNullOrEmpty(request.LastName))
                {
                    var latinLast = ToLatinName(request.LastName);
                    if (latinLast != oldSurname)
                    {
                        Track("Surname", oldSurname, latinLast);
                        user.Surname = latinLast;
                        nameChanged = true;
                    }
                }
                if (nameChanged)
                {
                    var newDisplay = $"{user.GivenName} {user.Surname}".Trim();
                    if (newDisplay != oldDisplayName)
                        Track("DisplayName", oldDisplayName, newDisplay);
                    user.DisplayName = newDisplay;
                }
            }

            // ── Account state ─────────────────────────────────────────────────
            switch (request.UserAccountControl?.ToLowerInvariant())
            {
                case "disabled" when oldIsEnabled != false:
                    Track("Enabled", oldIsEnabled?.ToString(), "False");
                    user.Enabled = false;
                    break;
                case "enabled" when oldIsEnabled != true:
                    Track("Enabled", oldIsEnabled?.ToString(), "True");
                    user.Enabled = true;
                    break;
            }

            user.Save();
            _logger.LogDebug("UserPrincipal saved | sam={Sam}", sam);

            // ── Extended attributes ───────────────────────────────────────────
            var dirty = false;

            if (request.Office is not null && request.Office != oldOffice)
            {
                Track("physicalDeliveryOfficeName", oldOffice, request.Office);
                SetOrClear(entry, "physicalDeliveryOfficeName", request.Office);
                dirty = true;
            }
            if (request.Company is not null && request.Company != oldCompany)
            {
                Track("company", oldCompany, request.Company);
                SetOrClear(entry, "company", request.Company);
                dirty = true;
            }
            if (request.Division is not null && request.Division != oldDivision)
            {
                Track("division", oldDivision, request.Division);
                SetOrClear(entry, "division", request.Division);
                dirty = true;
            }
            if (request.Description is not null && request.Description != oldDescription)
            {
                Track("description", oldDescription, request.Description);
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
                        Track("manager", oldManager, managerDn);
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

            // ── CN rename (DN = "CN={FirstName} {LastName}") ─────────────────
            if (nameChanged && _settings.UpdateDisplayName)
            {
                var newCn = $"CN={user.GivenName} {user.Surname}".Trim();
                _logger.LogInformation(
                    "Renaming CN: '{OldName}' → '{NewCn}' | sam={Sam}", entry.Name, newCn, sam);
                entry.Rename(newCn);
                entry.CommitChanges();
                _logger.LogInformation("CN renamed successfully | sam={Sam}", sam);
            }

            if (changes.Count == 0)
                _logger.LogInformation(
                    "UpdateUser completed — no changes detected | employeeID={EmployeeId}", request.EmployeeId);
            else
                _logger.LogInformation(
                    "UpdateUser completed | employeeID={EmployeeId}, sam={Sam}, changes={Count}",
                    request.EmployeeId, sam, changes.Count);

            // Re-fetch to return current state (updated DistinguishedName after rename)
            using var refreshed = FindBySam(context, sam);
            return new UserUpdateResult
            {
                User = MapToResponse(refreshed),
                Changes = changes
            };
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
    //   algorithm 0: first 3 of firstName + first 2 of lastName  (johdo)
    //   algorithm 1: first 2 of firstName + first 3 of lastName  (jodoe)
    //   algorithm 2: first 3 of firstName + first 3 of lastName  (johdoe)
    //
    // Returns (sam, algorithmIndex) so the caller can derive the matching email.
    // ─────────────────────────────────────────────────────────────────────────

    private (string Sam, int AlgorithmIndex) ResolveSamAccountName(
        PrincipalContext context, string firstName, string lastName)
    {
        var f = ToSamSafe(firstName);
        var l = ToSamSafe(lastName);

        var candidates = new[]
        {
            Take(f, 3) + Take(l, 2),   // 0
            Take(f, 2) + Take(l, 3),   // 1
            Take(f, 3) + Take(l, 3),   // 2
        };

        var tried = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < candidates.Length; i++)
        {
            var candidate = candidates[i];
            if (!tried.Add(candidate))
            {
                _logger.LogDebug(
                    "sAMAccountName '{Sam}' (algorithm {Index}) is a duplicate of a previous candidate, skipping",
                    candidate, i);
                continue;
            }

            if (!SamExists(context, candidate))
            {
                _logger.LogInformation(
                    "sAMAccountName resolved: '{Sam}' (algorithm {Index})", candidate, i);
                return (candidate, i);
            }

            _logger.LogDebug(
                "sAMAccountName '{Sam}' (algorithm {Index}) already taken, trying next",
                candidate, i);
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
        const string upper = "ABCDEFGHJKLMNPQRSTUVWXYZ";
        const string lower = "abcdefghjkmnpqrstuvwxyz";
        const string digits = "23456789";
        const string special = "!@#$%*";
        const string all = upper + lower + digits + special;

        var bytes = new byte[length * 2];
        RandomNumberGenerator.Fill(bytes);

        var pwd = new char[length];
        pwd[0] = upper[bytes[0] % upper.Length];
        pwd[1] = lower[bytes[1] % lower.Length];
        pwd[2] = digits[bytes[2] % digits.Length];
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

    /// <summary>Creates a DirectoryEntry scoped to a specific OU (for targeted searches).</summary>
    private DirectoryEntry CreateDirectoryEntryForOu(string ouDn) =>
        HasServiceAccount()
            ? new DirectoryEntry($"LDAP://{_settings.Domain}/{ouDn}",
                _settings.ServiceAccountUsername, _settings.ServiceAccountPassword)
            : new DirectoryEntry($"LDAP://{_settings.Domain}/{ouDn}");

    private bool HasServiceAccount() =>
        !string.IsNullOrWhiteSpace(_settings.ServiceAccountUsername);

    /// <summary>
    /// Finds a user by employeeID and loads them as UserPrincipal.
    /// When <paramref name="scopeOuDn"/> is provided the LDAP search is limited to that OU;
    /// otherwise the entire domain is searched.
    /// </summary>
    private UserPrincipal FindByEmployeeId(PrincipalContext context, string employeeId, string? scopeOuDn = null)
    {
        var root = scopeOuDn is not null
            ? CreateDirectoryEntryForOu(scopeOuDn)
            : CreateDirectoryEntry();

        using (root)
        {
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
    }

    /// <summary>Returns the DN of a user by employeeID, or null if not found.</summary>
    private string? FindDnByEmployeeId(string employeeId)
    {
        using var root = CreateDirectoryEntry();
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
        string? employeeId = null,
        string? office = null,
        string? company = null,
        string? division = null,
        string? description = null)
    {
        var entry = (DirectoryEntry)user.GetUnderlyingObject();
        var dirty = false;

        if (employeeId is not null) { SetOrClear(entry, "employeeID", employeeId); dirty = true; }
        if (office is not null) { SetOrClear(entry, "physicalDeliveryOfficeName", office); dirty = true; }
        if (company is not null) { SetOrClear(entry, "company", company); dirty = true; }
        if (division is not null) { SetOrClear(entry, "division", division); dirty = true; }
        if (description is not null) { SetOrClear(entry, "description", description); dirty = true; }

        if (dirty) entry.CommitChanges();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Schema helpers
    // ─────────────────────────────────────────────────────────────────────────

    // Cache schema lookups for the process lifetime — the AD schema is immutable at runtime.
    private static readonly ConcurrentDictionary<string, bool> _schemaCache =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Returns true when <paramref name="ldapDisplayName"/> is defined in the AD schema.
    /// Results are cached so the schema NC is only queried once per attribute name per process.
    /// Extension attributes (extensionAttribute1–15) require the Exchange schema extension;
    /// they will not exist on a plain AD domain without Exchange.
    /// </summary>
    private bool SchemaHasAttribute(string ldapDisplayName)
    {
        return _schemaCache.GetOrAdd(ldapDisplayName, name =>
        {
            try
            {
                using var rootDse = HasServiceAccount()
                    ? new DirectoryEntry($"LDAP://{_settings.Domain}/RootDSE",
                        _settings.ServiceAccountUsername, _settings.ServiceAccountPassword)
                    : new DirectoryEntry($"LDAP://{_settings.Domain}/RootDSE");

                var schemaDn = rootDse.Properties["schemaNamingContext"].Value?.ToString();
                if (schemaDn is null)
                {
                    _logger.LogWarning(
                        "Cannot read schemaNamingContext from RootDSE — " +
                        "assuming attribute '{Attr}' is absent", name);
                    return false;
                }

                using var schemaRoot = HasServiceAccount()
                    ? new DirectoryEntry($"LDAP://{_settings.Domain}/{schemaDn}",
                        _settings.ServiceAccountUsername, _settings.ServiceAccountPassword)
                    : new DirectoryEntry($"LDAP://{_settings.Domain}/{schemaDn}");

                using var searcher = new DirectorySearcher(schemaRoot)
                {
                    Filter = $"(&(objectClass=attributeSchema)(lDAPDisplayName={name}))",
                    SearchScope = SearchScope.OneLevel
                };
                searcher.PropertiesToLoad.Add("lDAPDisplayName");

                var exists = searcher.FindOne() is not null;
                _logger.LogDebug("Schema check: attribute '{Attr}' exists={Exists}", name, exists);
                return exists;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Schema check failed for attribute '{Attr}' — treating as absent", name);
                return false;
            }
        });
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Group membership
    // ─────────────────────────────────────────────────────────────────────────

    private void AddUserToGroups(UserPrincipal user, string? office)
    {
        var groupNames = new List<string>(_settings.GlobalGroups);

        if (office is not null &&
            _settings.OfficeGroupMappings.TryGetValue(office, out var officeGroups))
        {
            groupNames.AddRange(officeGroups);
            _logger.LogInformation(
                "Office group mapping found for '{Office}': [{Groups}]",
                office, string.Join(", ", officeGroups));
        }
        else
        {
            _logger.LogInformation(
                "No office group mapping for '{Office}'; only GlobalGroups will be applied",
                office ?? "(none)");
        }

        if (groupNames.Count == 0)
        {
            _logger.LogInformation("No groups configured; skipping group membership step");
            return;
        }

        using var context = CreateDomainContext();

        foreach (var groupName in groupNames)
        {
            var group = GroupPrincipal.FindByIdentity(context, groupName);
            if (group is null)
            {
                _logger.LogWarning("Group '{Group}' not found in AD; skipping", groupName);
                continue;
            }

            group.Members.Add(user);
            group.Save();
            _logger.LogInformation(
                "Added '{Sam}' to group '{Group}'", user.SamAccountName, groupName);
        }
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
            UserPrincipalName = user.UserPrincipalName ?? string.Empty,
            EmployeeId        = entry.Properties["employeeID"].Value?.ToString() ?? string.Empty,
            EmployeeNumber    = entry.Properties["employeeNumber"].Value?.ToString(),
            FirstName         = user.GivenName,
            LastName          = user.Surname,
            DisplayName       = user.DisplayName ?? string.Empty,
            Email             = user.EmailAddress,
            Office            = entry.Properties["physicalDeliveryOfficeName"].Value?.ToString(),
            Department        = entry.Properties["department"].Value?.ToString(),
            Company           = entry.Properties["company"].Value?.ToString(),
            Division          = entry.Properties["division"].Value?.ToString(),
            Title             = entry.Properties["title"].Value?.ToString(),
            Manager           = entry.Properties["manager"].Value?.ToString(),
            Description       = entry.Properties["description"].Value?.ToString(),
            IsEnabled         = user.Enabled ?? false,
            DistinguishedName = user.DistinguishedName ?? string.Empty
        };
    }
}
