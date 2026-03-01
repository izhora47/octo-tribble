# LDAP API — Active Directory & Exchange Management

A .NET 10 Minimal API that runs as a **Windows Service** on a domain-joined machine. It exposes a REST interface for automating Active Directory user provisioning, attribute management, Exchange mailbox control, and new-user notifications.

---

## Application structure

The project follows the standard .NET Minimal API pattern — no controllers, no MVC pipeline. The entry point is `Program.cs`, which wires everything together and then delegates to dedicated files.

```
ldap-api/
├── Program.cs                   — entry point: host setup, DI registration, route mapping
├── appsettings.json             — all runtime configuration (AD, SMTP, groups, logging)
│
├── Configuration/
│   ├── AdSettings.cs            — strongly-typed binding for the AdSettings section
│   └── SmtpSettings.cs          — strongly-typed binding for the SmtpSettings section
│
├── Endpoints/
│   ├── UserEndpoints.cs         — defines the /api/users route group
│   └── ExchangeEndpoints.cs     — defines the /api/exchange route group
│
├── Models/
│   ├── ChangeRecord.cs          — record(Field, OldValue, NewValue) used by update notifications
│   ├── Requests/
│   │   ├── CreateUserRequest.cs
│   │   ├── UpdateUserRequest.cs
│   │   └── ExchangeMailboxRequest.cs
│   └── Responses/
│       ├── ApiResponse.cs       — generic envelope: { success, message, data }
│       ├── CreateUserResponse.cs
│       ├── UserResponse.cs
│       ├── UserUpdateResult.cs  — wraps UserResponse + list of ChangeRecords
│       └── ExchangeMailboxResponse.cs
│
└── Services/
    ├── IAdService.cs / AdService.cs         — all AD operations
    ├── IExchangeService.cs / ExchangeService.cs — Exchange PowerShell remoting
    └── IEmailService.cs / EmailService.cs   — SMTP notification emails
```

### How a request flows through the application

1. `Program.cs` registers configuration (`AdSettings`, `SmtpSettings`), services (`AdService`, `ExchangeService`, `EmailService`), and OpenAPI. It then calls `app.MapUserEndpoints()` and `app.MapExchangeEndpoints()`.
2. Each of those extension methods (in `Endpoints/`) registers route handlers for its group. Route handlers are plain `static async Task<IResult>` methods — no controller base class needed.
3. A route handler receives the deserialized request model and the relevant service(s) from the DI container via parameter injection. It calls the service, wraps the result in `ApiResponse<T>`, and returns the appropriate `IResult`.
4. Services (`AdService`, `ExchangeService`, `EmailService`) each take their configuration via `IOptions<T>` and a typed `ILogger<T>`. They contain all business logic and have no dependency on the HTTP layer.
5. All AD operations run inside `Task.Run(...)` to avoid blocking the ASP.NET Core thread pool with synchronous `System.DirectoryServices` calls.

### Logging

Serilog is configured programmatically in `Program.cs` (`builder.Services.AddSerilog`). Logs go to both the console and a rolling daily file (`logs/ldap-api-YYYYMMDD.log`). The log path is read from `LogSettings:FilePath`; relative paths are anchored to the executable directory so the log lands in the right place whether running via `dotnet run` or as a Windows Service. `Microsoft.*` infrastructure logs are suppressed below Warning to reduce noise, except for `Microsoft.Hosting.Lifetime` which is kept at Information so startup/shutdown messages remain visible.

---

## Functionality

### Create user — `POST /api/users`

**Steps performed in order:**

1. **Duplicate employeeID check.** A `DirectorySearcher` query runs against AD with `(&(objectClass=user)(objectCategory=person)(employeeID=...))`. If a match is found the request is rejected with `409 Conflict` before any write is attempted.

2. **Cyrillic transliteration.** `FirstName` and `LastName` may be supplied in Cyrillic. Before any derived attribute is computed, names are transliterated character-by-character using a full Russian-alphabet table, diacritics are stripped via Unicode `FormD` normalization, and the result is reduced to ASCII letters and digits. The original Unicode spelling is stored in `GivenName`, `Surname`, `DisplayName`, and `CN` unchanged.

3. **sAMAccountName generation — 3 algorithms.**

   | Algorithm | Formula | Example — John Doe |
   |-----------|---------|-------------------|
   | 0 | first 3 of firstName + first 2 of lastName | `johdo` |
   | 1 | first 2 of firstName + first 3 of lastName | `jodoe` |
   | 2 | first 3 of firstName + first 3 of lastName | `johdoe` |

   Algorithms are tried in order. Each candidate is checked against AD via `UserPrincipal.FindByIdentity`. If two algorithms produce the same string (can happen with very short names) the duplicate is skipped and does not count as a separate attempt. If all three unique candidates are already taken the request returns `409 Conflict`.

4. **Consistent suffix across all unique attributes.** The algorithm index (0, 1, or 2) that produced the available SAM drives a suffix applied to `CN`, `userPrincipalName`, and `email`, keeping all four attributes unique as a set:

   | Algorithm | `sAMAccountName` | `CN` | `userPrincipalName` | `email` |
   |-----------|-----------------|------|---------------------|---------|
   | 0 | `johdo` | `John Doe` | `john.doe@company.com` | `john.doe@company.com` |
   | 1 | `jodoe` | `John Doe1` | `john.doe1@company.com` | `john.doe1@company.com` |
   | 2 | `johdoe` | `John Doe2` | `john.doe2@company.com` | `john.doe2@company.com` |

   `DisplayName` never receives a suffix — it is always `"FirstName LastName"` and is the human-readable name in Outlook and the GAL.

5. **Password generation.** A 12-character password is built using `RandomNumberGenerator.Fill` to ensure cryptographic quality. The alphabet excludes visually ambiguous characters (I, l, O, 0, 1). Complexity is guaranteed by reserving the first four positions for one uppercase letter, one lowercase letter, one digit, and one special character (`!@#$%*`), then shuffling with Fisher-Yates.

6. **OU resolution.** Target OU is resolved with the following priority:
   - `targetOu` field in the request (explicit DN override)
   - `AdSettings.OfficeOuMappings[office]` — office name mapped to an OU DN in config
   - `AdSettings.DefaultUserOu` — fallback

7. **Account creation.** `UserPrincipal` is created in the resolved OU with `Name` (CN), `GivenName`, `Surname`, `DisplayName`, `SamAccountName`, `UserPrincipalName`, `EmailAddress`. Account state defaults to **enabled**; pass `"userAccountControl": "disabled"` to create the account disabled. The account is saved, then additional LDAP attributes are written via `DirectoryEntry.Properties`: `employeeID`, `physicalDeliveryOfficeName`, `company`, `division`, `description`.

8. **Creation-only attributes.** After saving, two attributes are always set via `DirectoryEntry`:
   - `pwdLastSet = 0` — forces the user to change their password at next logon.
   - `extensionAttribute15 = "1"` — monitored by Windows Scheduler to provision the SfB (Skype for Business) account.

9. **Group membership.** After the account is saved, the user is added to groups in this order:
   - All groups listed in `AdSettings.GlobalGroups` (applied to every new user)
   - All groups in `AdSettings.OfficeGroupMappings[office]` (for the user's office, if a mapping exists)

   If no office mapping exists, only `GlobalGroups` are applied. Each group is located via `GroupPrincipal.FindByIdentity`. If a group name is not found in AD, a warning is logged and that group is skipped — the rest of the creation flow continues normally.

10. **Email notification.** After the AD operation completes successfully:
    - Recipients in `SmtpSettings.OfficeRecipients[office]` receive a notification with the user's name, email, login (SAM), employeeID, and office — **without** the password.
    - The admin address (`SmtpSettings.MailTo`) receives the same body **including the password**.
    - If no office mapping exists in `OfficeRecipients`, only the admin address is notified.
    - Email failures are caught, logged as errors, and never affect the HTTP response or the AD result.

**Response:** `201 Created` with the generated `samAccountName`, `email`, and `password`.

---

### Update user — `PUT /api/users`

The user is located by `employeeID`. When `office` is included in the request and has a matching `OfficeOuMappings` entry, the LDAP search is scoped to that OU for efficiency. Otherwise the entire domain is searched.

**Disabled-user guard.** Before any write is attempted, the user's `distinguishedName` is checked. If it contains `OU=Users Disabled`, the request is rejected with `404 Not Found` — disabled accounts cannot be updated via this API.

**Only non-null, non-empty request fields are written.** `firstName` and `lastName` are skipped if they are null or an empty string. For each field that actually changes, the old and new values are logged:
```
[physicalDeliveryOfficeName] 'Moscow' → 'NRW'
[company] '(empty)' → 'Acme Corp'
```

**Fields that can be updated:**

| Request field | LDAP attribute | Notes |
|---|---|---|
| `firstName` | `givenName` | Skipped if null or empty; only when `UpdateDisplayName: true` |
| `lastName` | `sn` | Skipped if null or empty; only when `UpdateDisplayName: true` |
| `office` | `physicalDeliveryOfficeName` | Also used to scope the LDAP search |
| `company` | `company` | |
| `division` | `division` | |
| `description` | `description` | |
| `userAccountControl` | `userAccountControl` | Accepts `"enabled"` or `"disabled"`; null = no change |
| `managerEmployeeId` | `manager` | Resolved to DN by employeeID lookup |

**Name and CN rename (`UpdateDisplayName: true`).** When `firstName` or `lastName` changes and `UpdateDisplayName` is `true` in settings:
1. `GivenName`, `Surname`, and `DisplayName` are updated.
2. The CN is renamed to `CN={FirstName} {LastName}` via `DirectoryEntry.Rename(...)`, which automatically updates the `DistinguishedName` as well.
3. The user is re-fetched after the rename so the response reflects the new DN.

When `UpdateDisplayName: false`, `firstName` and `lastName` in the request are silently ignored.

**Manager assignment.** If `managerEmployeeId` is supplied, a `DirectorySearcher` query finds the manager's `distinguishedName` by their `employeeID`, and writes it to the `manager` attribute. If the manager is not found in AD, a warning is logged and the `manager` attribute is left unchanged — the update does not fail.

**Account state.** `userAccountControl: "disabled"` disables the account; `"enabled"` re-enables it. Any other value (including `null`) leaves the state unchanged. Accounts are never deleted through this API.

**Email notification.** A notification is sent to the admin address (`SmtpSettings.MailTo`) **only when at least one field actually changed**. If the request contained values identical to what is already stored in AD, the request is logged as "no changes detected" and no email is sent. The email body lists each changed field with its old and new value:
```
User account updated:

Login    - johdo
ID       - 123456789

Changes:
[physicalDeliveryOfficeName]
  Old value: Moscow
  New value: NRW
[company]
  Old value: (empty)
  New value: Acme Corp
```

**Response:** `200 OK` with the current state of all tracked attributes, including the (possibly updated) `distinguishedName`.

---

### Get user by sAMAccountName — `GET /api/users/by-sam/{samAccountName}`

Calls `UserPrincipal.FindByIdentity` with `IdentityType.SamAccountName`. Returns `404` if not found.

### Get user by employeeID — `GET /api/users/by-employee-id/{employeeId}`

Runs a `DirectorySearcher` query with `(&(objectClass=user)(objectCategory=person)(employeeID=...))`, resolves the DN, then loads the `UserPrincipal`. Returns `404` if not found.

**Response for both GET endpoints — `UserResponse` shape:**

| Field | Source |
|---|---|
| `samAccountName` | `sAMAccountName` |
| `userPrincipalName` | `userPrincipalName` |
| `employeeId` | `employeeID` |
| `employeeNumber` | `employeeNumber` |
| `firstName` | `givenName` |
| `lastName` | `sn` |
| `displayName` | `displayName` |
| `email` | `mail` |
| `office` | `physicalDeliveryOfficeName` |
| `department` | `department` |
| `company` | `company` |
| `division` | `division` |
| `title` | `title` |
| `manager` | `manager` (DN of manager) |
| `description` | `description` |
| `isEnabled` | derived from `userAccountControl` |
| `distinguishedName` | `distinguishedName` |

---

### Enable mailbox — `POST /api/exchange/mailbox/enable`

Connects to the Exchange Management Shell via WS-Man (`WSManConnectionInfo` → `RunspaceFactory.CreateRunspace`). Steps:

1. **`Get-Mailbox`** — checks whether a mailbox already exists for the given `sAMAccountName`. The `wasAlreadyEnabled` flag in the response reflects this.
2. **`Enable-Mailbox`** — runs only if no mailbox was found in step 1.
3. **`Set-Mailbox -HiddenFromAddressListsEnabled $false`** — always runs, ensures the mailbox is visible in the Global Address List.
4. **`Set-CASMailbox -ActiveSyncEnabled $true -OWAforDevicesEnabled $true -OWAEnabled $true`** — always runs, enables client access protocols.
5. **Verify and notify.** `MailboxEnabled = true` in the response confirms the mailbox is active (any Exchange cmdlet failure throws an exception). The user is then looked up in AD by `sAMAccountName` to retrieve their `userPrincipalName`. A welcome / onboarding email is sent to that address (fire-and-forget; email failure does not affect the HTTP response).

**Note on UPN = email address.** `userPrincipalName` and the `mail` attribute are always set to the same value during account creation (`firstname.lastname@EmailDomain`), so the UPN is used directly as the onboarding email destination.

If `ServiceAccountUsername` is empty, Kerberos is used with `PSCredential.Empty` (the Windows service identity). Otherwise, `Negotiate` auth is used with the configured credentials.

### Disable mailbox — `POST /api/exchange/mailbox/disable`

Runs `Disable-Mailbox -Identity {sam} -Confirm:$false`. The mailbox data is removed from Exchange; the AD account itself is not touched.

---

## Configuration reference

```json
{
  "LogSettings": {
    "FilePath": "logs/ldap-api-.log"
  },
  "AdSettings": {
    "Domain": "company.local",
    "EmailDomain": "company.com",
    "DefaultUserOu": "OU=Users,DC=company,DC=local",
    "ServiceAccountUsername": "",
    "ServiceAccountPassword": "",
    "ExchangePowerShellUri": "http://exchange-server.company.local/PowerShell",
    "UpdateDisplayName": false,
    "OfficeOuMappings": {
      "NRW":    "OU=Users,OU=NRW,DC=company,DC=local",
      "Moscow": "OU=Users,OU=Moscow,DC=company,DC=local"
    },
    "GlobalGroups": ["Domain Users", "VPN-Access"],
    "OfficeGroupMappings": {
      "NRW":    ["NRW-Staff", "NRW-Printers"],
      "Moscow": ["Moscow-Staff"]
    }
  },
  "SmtpSettings": {
    "Server": "smtp.company.local",
    "Port": 25,
    "MailFrom": "it-noreply@company.com",
    "MailTo": "it-team@company.com",
    "OfficeRecipients": {
      "NRW":    ["hr-nrw@company.com", "manager-nrw@company.com"],
      "Moscow": ["hr-moscow@company.com"]
    }
  }
}
```

| Setting | Description |
|---------|-------------|
| `LogSettings.FilePath` | Rolling log file path. Relative paths are anchored to the executable directory. The date is inserted before the extension — `logs/ldap-api-20260301.log`. |
| `AdSettings.Domain` | Internal AD domain used for `PrincipalContext` binding. |
| `AdSettings.EmailDomain` | External mail domain. Used for both the `mail` attribute and `userPrincipalName` — they are always identical. |
| `AdSettings.DefaultUserOu` | Fallback OU (DN format) for new accounts when no `targetOu` or `OfficeOuMappings` match. |
| `AdSettings.ServiceAccountUsername` | Account used to bind to AD and authenticate to Exchange. Leave empty to use the Windows service identity (Kerberos). |
| `AdSettings.ServiceAccountPassword` | Password for the above account. |
| `AdSettings.ExchangePowerShellUri` | WS-Man endpoint of Exchange Management Shell, e.g. `http://exchange.company.local/PowerShell`. |
| `AdSettings.UpdateDisplayName` | `true` — name fields in update requests are written and the CN is renamed. `false` — name fields in update requests are ignored. |
| `AdSettings.OfficeOuMappings` | Maps office name → OU DN. Used to place new accounts in the right OU and to scope update searches. |
| `AdSettings.GlobalGroups` | AD group names that every new user is added to. |
| `AdSettings.OfficeGroupMappings` | Maps office name → list of AD group names. Applied in addition to `GlobalGroups`. |
| `SmtpSettings.Server` | SMTP relay hostname or IP. |
| `SmtpSettings.Port` | SMTP port (25 for relay, 587 for submission). |
| `SmtpSettings.MailFrom` | Sender address on all outgoing notifications. |
| `SmtpSettings.MailTo` | Admin recipient. Receives all notifications and is the only address that gets the generated password. |
| `SmtpSettings.OfficeRecipients` | Maps office name → list of email addresses. Receive the create-user notification without the password. |

**ServiceAccountUsername / ServiceAccountPassword note.** These are credentials for AD/Exchange binding — they are **not** the Windows service logon account. The logon account is configured separately via `sc.exe config obj=` or Services Manager. If the service identity already has the required AD and Exchange permissions, leave both fields empty.

---

## Prerequisites

- Windows Server joined to the target domain
- .NET 10 Runtime (or use self-contained publish)
- Service account with:
  - **AD**: Create/modify user objects in the target OUs; read access to look up groups and manager DNs
  - **Exchange**: `Recipient Management` role for `Enable-Mailbox`, `Disable-Mailbox`, `Set-Mailbox`, `Set-CASMailbox`
- WinRM enabled on the Exchange server

---

## Running for development

```powershell
cd C:\path\to\ldap-api
dotnet run
```

The API starts at `http://localhost:5253`. Open the interactive explorer at http://localhost:5253/scalar/v1 (or http://localhost:5253/swagger — it redirects).

---

## Publishing

```powershell
# Self-contained — no .NET runtime required on the target machine
dotnet publish -c Release -r win-x64 --self-contained true -o C:\LDAP_API

# Framework-dependent — requires .NET 10 runtime on the target machine
dotnet publish -c Release -r win-x64 --self-contained false -o C:\LDAP_API
```

After publishing, edit `C:\LDAP_API\appsettings.json` with production values.

---

## Installing and managing as a Windows Service

All `sc.exe` commands require an **elevated** command prompt.

### Install

```cmd
sc.exe create "LdapApi" ^
    binpath= "C:\LDAP_API\ldap-api.exe --urls http://localhost:5000" ^
    start= auto ^
    displayname= "LDAP API"

sc.exe description "LdapApi" "Active Directory and Exchange mailbox management REST API"
```

> `sc.exe` requires a space after every `=`.

### Recovery on failure

```cmd
sc.exe failure "LdapApi" reset= 86400 actions= restart/5000/restart/10000/restart/30000
```

### Set the service logon account

```cmd
sc.exe config "LdapApi" obj= "COMPANY\svc-ldapapi" password= "s3cr3t"
```

### Start / Stop / Status / Uninstall

```cmd
sc.exe start  "LdapApi"
sc.exe stop   "LdapApi"
sc.exe query  "LdapApi"

sc.exe stop   "LdapApi"
sc.exe delete "LdapApi"
```

---

## API reference

### GET /health

```
GET http://localhost:5000/health
```

---

### POST /api/users — Create user

**Request**
```json
{
  "employeeId": "123456789",
  "firstName": "John",
  "lastName": "Doe",
  "office": "NRW",
  "company": "ExampleCompany",
  "division": "Engineering",
  "description": "Test account",
  "targetOu": null,
  "userAccountControl": null
}
```

Omit `userAccountControl` or set it to anything other than `"disabled"` to create the account **enabled** (default). Set to `"disabled"` to create the account disabled.

Cyrillic names are accepted:
```json
{
  "employeeId": "987654321",
  "firstName": "Иван",
  "lastName": "Петров",
  "office": "Moscow"
}
```

**Response 201** — algorithm 0
```json
{
  "success": true,
  "message": null,
  "data": {
    "status": "created",
    "employeeId": "123456789",
    "samAccountName": "johdo",
    "email": "john.doe@company.com",
    "password": "Kp3!mNzR7@qW"
  }
}
```

**Response 201** — algorithm 1 (`johdo` was taken; CN, UPN, email all get suffix `1`)
```json
{
  "success": true,
  "message": null,
  "data": {
    "status": "created",
    "employeeId": "123456789",
    "samAccountName": "jodoe",
    "email": "john.doe1@company.com",
    "password": "Rz7#mKpN2@qW"
  }
}
```

**Response 409** — employeeID already in use
```json
{
  "success": false,
  "message": "A user with employeeID '123456789' already exists in Active Directory."
}
```

**Response 409** — all SAM candidates taken
```json
{
  "success": false,
  "message": "All generated sAMAccountName candidates for 'John Doe' are already taken. Please create the account manually."
}
```

---

### PUT /api/users — Update user

Only non-null, non-empty fields are written. `employeeId` is required and is the lookup key.
Returns `404` if the user is not found or is in `OU=Users Disabled`.

**Update attributes**
```json
{
  "employeeId": "123456789",
  "office": "NRW",
  "company": "ExampleCompany",
  "division": "New Division",
  "description": "Updated description",
  "managerEmployeeId": "000000001"
}
```

**Rename** (only applied when `UpdateDisplayName: true` in config)
```json
{
  "employeeId": "123456789",
  "firstName": "Jonathan",
  "lastName": "Doe"
}
```

**Disable account**
```json
{
  "employeeId": "123456789",
  "userAccountControl": "disabled"
}
```

**Re-enable account**
```json
{
  "employeeId": "123456789",
  "userAccountControl": "enabled"
}
```

**Response 200**
```json
{
  "success": true,
  "message": "User updated successfully.",
  "data": {
    "samAccountName": "johdo",
    "userPrincipalName": "john.doe@company.com",
    "employeeId": "123456789",
    "employeeNumber": null,
    "firstName": "John",
    "lastName": "Doe",
    "displayName": "John Doe",
    "email": "john.doe@company.com",
    "office": "NRW",
    "department": "Engineering",
    "company": "ExampleCompany",
    "division": "New Division",
    "title": "Software Engineer",
    "manager": "CN=Jane Smith,OU=Users,OU=NRW,DC=company,DC=local",
    "description": "Updated description",
    "isEnabled": true,
    "distinguishedName": "CN=John Doe,OU=Users,OU=NRW,DC=company,DC=local"
  }
}
```

**Response 404** — employeeID not found or user is in disabled OU
```json
{
  "success": false,
  "message": "No user with employeeID '123456789' found in Active Directory."
}
```

---

### GET /api/users/by-sam/{samAccountName}

```
GET http://localhost:5000/api/users/by-sam/johdo
```

---

### GET /api/users/by-employee-id/{employeeId}

```
GET http://localhost:5000/api/users/by-employee-id/123456789
```

---

### POST /api/exchange/mailbox/enable

Enables the Exchange mailbox and sends a welcome email to the user's UPN address.

```json
{ "samAccountName": "johdo" }
```

**Response 200** — mailbox created, onboarding email sent
```json
{
  "success": true,
  "message": null,
  "data": {
    "samAccountName": "johdo",
    "mailboxEnabled": true,
    "wasAlreadyEnabled": false,
    "status": "Mailbox enabled and configured successfully."
  }
}
```

**Response 200** — mailbox already existed (settings still reapplied, onboarding email still sent)
```json
{
  "success": true,
  "message": null,
  "data": {
    "samAccountName": "johdo",
    "mailboxEnabled": true,
    "wasAlreadyEnabled": true,
    "status": "Mailbox was already enabled. Configuration settings applied."
  }
}
```

---

### POST /api/exchange/mailbox/disable

```json
{ "samAccountName": "johdo" }
```

**Response 200**
```json
{
  "success": true,
  "message": null,
  "data": {
    "samAccountName": "johdo",
    "mailboxEnabled": false,
    "wasAlreadyEnabled": false,
    "status": "Mailbox disabled successfully."
  }
}
```
