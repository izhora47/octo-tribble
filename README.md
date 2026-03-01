# LDAP API — Active Directory & Exchange Management

A .NET 10 Minimal API designed to run as a **Windows Service** on a domain-joined Windows machine. It provides a REST interface for provisioning and managing Active Directory user accounts and Exchange mailboxes.

---

## What it does

| Operation | Description |
|-----------|-------------|
| **Create AD user** | Generates a unique `sAMAccountName`, `CN`, `userPrincipalName`, and `email` — all using the same 3-algorithm strategy. Checks for duplicate `employeeID` before creation. |
| **Update AD user** | Finds user by `employeeID`, updates any supplied attributes; optionally renames `DisplayName` and `CN`/`DN` when names change (`UpdateDisplayName` setting). |
| **Disable/Enable AD user** | Triggered by the update endpoint with `userAccountControl: "disabled"/"enabled"` — accounts are **never deleted**. |
| **Enable mailbox** | Checks for an existing mailbox, creates it if absent, unhides from address lists, enables ActiveSync + OWA. |
| **Disable mailbox** | Runs `Disable-Mailbox` against the Exchange PowerShell endpoint. |

---

## Architecture

```
ldap-api/
├── Configuration/
│   └── AdSettings.cs            — strongly-typed settings bound from appsettings.json
├── Endpoints/
│   ├── UserEndpoints.cs         — /api/users route group
│   └── ExchangeEndpoints.cs     — /api/exchange route group
├── Models/
│   ├── Requests/
│   │   ├── CreateUserRequest.cs
│   │   ├── UpdateUserRequest.cs
│   │   └── ExchangeMailboxRequest.cs
│   └── Responses/
│       ├── ApiResponse.cs       — generic wrapper { success, message, data }
│       ├── CreateUserResponse.cs
│       ├── UserResponse.cs
│       └── ExchangeMailboxResponse.cs
├── Services/
│   ├── IAdService / AdService           — AD operations via System.DirectoryServices
│   └── IExchangeService / ExchangeService — Exchange via PowerShell remoting
├── Program.cs
└── appsettings.json
```

---

## Key logic

### Cyrillic transliteration

`FirstName` and `LastName` can be provided in Cyrillic. Before any attribute is derived, names are transliterated to Latin (full Russian alphabet map), diacritics are stripped, and only ASCII letters/digits are kept. This applies to `sAMAccountName`, `userPrincipalName`, and `email` local-parts — the original Unicode spelling is stored in `GivenName`, `Surname`, `DisplayName`, and `CN`.

### sAMAccountName generation — 3 algorithms

| Algorithm | Formula | Example (John Doe) |
|-----------|---------|-------------------|
| **0** | first **3** of firstName + first **2** of lastName | `johdo` |
| **1** | first **2** of firstName + first **3** of lastName | `jodoe` |
| **2** | first **3** of firstName + first **3** of lastName | `johdoe` |

Candidates are tried in order. Duplicate candidates (possible with very short names) are skipped. If all three unique candidates are already taken the API returns `409 Conflict`.

### Consistent naming across attributes

The algorithm index that produces the available SAM is also used to derive the `CN`, `userPrincipalName`, and `email`, so all four attributes stay unique together:

| Algorithm | `sAMAccountName` | `CN` / `DN` | `userPrincipalName` | `email` |
|-----------|-----------------|-------------|---------------------|---------|
| 0 | `johdo` | `CN=John Doe` | `john.doe@company.local` | `john.doe@company.com` |
| 1 | `jodoe` | `CN=John Doe1` | `john.doe1@company.local` | `john.doe1@company.com` |
| 2 | `johdoe` | `CN=John Doe2` | `john.doe2@company.local` | `john.doe2@company.com` |

`DisplayName` is always `"John Doe"` (no suffix) — it is the human-readable name shown in Outlook/Teams and does not need to be unique.

### employeeID uniqueness check

Before any account creation work begins, the API searches AD for a user with the same `employeeID`. If one exists, a `409 Conflict` is returned immediately.

### OU resolution (create)

Target OU is resolved in this priority order:

1. `targetOu` field in the request (explicit override)
2. `OfficeOuMappings[office]` from `appsettings.json` (office name → OU)
3. `DefaultUserOu` from `appsettings.json`

### Update — change detection and logging

Only non-null request fields are written. For each changed field the service logs:
```
[GivenName] 'Ivan' → 'Иван'
[physicalDeliveryOfficeName] 'Moscow' → 'NRW'
```

### Update — CN rename

When `UpdateDisplayName: true` and `FirstName` or `LastName` changes, the service:
1. Updates `GivenName`, `Surname`, `DisplayName`
2. Renames the `CN` via `DirectoryEntry.Rename("CN=NewFirst NewLast")` — which also updates the `DistinguishedName`
3. Re-fetches the user after rename and returns the new DN in the response

When `UpdateDisplayName: false`, name fields are ignored entirely on update.

### Manager assignment

Supply `managerEmployeeId` in an update request. The service looks up the manager's `DistinguishedName` by their `employeeID` and writes it to the `manager` LDAP attribute. If the manager is not found a warning is logged and the attribute is left unchanged.

### Password generation

12-character random password containing at least one uppercase letter, one lowercase letter, one digit, and one special character (`!@#$%*`). Visually ambiguous characters (I, l, O, 0, 1) are excluded. Uses `RandomNumberGenerator` + Fisher-Yates shuffle for cryptographic quality.

### ServiceAccountUsername / ServiceAccountPassword — clarification

These credentials are used to:
1. Bind to Active Directory (`PrincipalContext` and `DirectoryEntry` / `DirectorySearcher`)
2. Authenticate against the Exchange PowerShell remoting endpoint (`Negotiate` auth)

They are **not** the Windows service logon account. That is configured separately via Services Manager or `sc.exe` at installation time.

Leave both fields **empty** if the Windows service identity already has the necessary AD and Exchange permissions (gMSA or domain account configured via `sc.exe config obj=`). In that case the API uses Kerberos with the process identity.

---

## Prerequisites

- Windows Server joined to the target domain
- .NET 10 Runtime (or use self-contained publish)
- A service/admin account with:
  - **AD**: permission to create/modify user objects in the target OU
  - **Exchange**: `Recipient Management` role (for `Enable-Mailbox`, `Disable-Mailbox`, `Set-Mailbox`, `Set-CASMailbox`)
- WinRM enabled on the Exchange server (required for PowerShell remoting)

---

## Configuration

Edit `appsettings.json` (or use environment variables — prefix `AdSettings__`):

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
      "NRW": "OU=Users,OU=NRW,DC=company,DC=local",
      "Moscow": "OU=Users,OU=Moscow,DC=company,DC=local"
    }
  }
}
```

| Setting | Description |
|---------|-------------|
| `LogSettings.FilePath` | Rolling log file path. Relative paths are anchored to the executable directory. Date is inserted before the extension (`ldap-api-20260228.log`). |
| `Domain` | Internal AD domain — used for `PrincipalContext` binding and the `@domain` part of `userPrincipalName` |
| `EmailDomain` | Mail domain for the generated `mail` attribute (often differs from AD domain) |
| `DefaultUserOu` | Fallback OU for new accounts when no `targetOu` or `OfficeOuMappings` match |
| `ServiceAccountUsername` | LDAP/Exchange bind account. Leave empty to use the Windows service identity (Kerberos) |
| `ServiceAccountPassword` | Password for the above account |
| `ExchangePowerShellUri` | WS-Man endpoint of Exchange Management Shell |
| `UpdateDisplayName` | `true` = allow updating GivenName/Surname/DisplayName/CN on update calls; `false` = name fields are ignored on update |
| `OfficeOuMappings` | Dictionary mapping office names (from the `office` request field) to target OUs |

---

## Running for development

```powershell
cd C:\path\to\ldap-api
dotnet run
```

The API starts at `http://localhost:5253` (see `launchSettings.json`).

Open the interactive API explorer: http://localhost:5253/scalar/v1
(or navigate to http://localhost:5253/swagger — it redirects)

---

## Publishing to C:\LDAP_API

```powershell
# Self-contained — no .NET runtime required on the target machine
dotnet publish -c Release -r win-x64 --self-contained true -o C:\LDAP_API

# Framework-dependent — requires .NET 10 runtime installed on the target machine
dotnet publish -c Release -r win-x64 --self-contained false -o C:\LDAP_API
```

After publishing, edit `C:\LDAP_API\appsettings.json` with the production values.

---

## Installing and managing as a Windows Service

All `sc.exe` commands must be run in an **elevated** (Administrator) command prompt.

### Install

```cmd
sc.exe create "LdapApi" ^
    binpath= "C:\LDAP_API\ldap-api.exe --urls http://localhost:5000" ^
    start= auto ^
    displayname= "LDAP API"

sc.exe description "LdapApi" "Active Directory and Exchange mailbox management REST API"
```

> Note: `sc.exe` requires a space after the `=` sign for every parameter.

### Set recovery options (restart on failure)

```cmd
sc.exe failure "LdapApi" reset= 86400 actions= restart/5000/restart/10000/restart/30000
```

### Configure the service logon account (optional)

By default the service runs as `LocalSystem`. For AD operations you will usually want a dedicated domain account:

```cmd
sc.exe config "LdapApi" obj= "COMPANY\svc-ldapapi" password= "s3cr3t"
```

Alternatively set it in **Services Manager** → Properties → Log On tab.

### Start / Stop / Status

```cmd
sc.exe start  "LdapApi"
sc.exe stop   "LdapApi"
sc.exe query  "LdapApi"
```

### Uninstall

```cmd
sc.exe stop   "LdapApi"
sc.exe delete "LdapApi"
```

---

## API Reference & Test JSON

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
  "targetOu": null
}
```

Cyrillic names are also accepted:
```json
{
  "employeeId": "987654321",
  "firstName": "Иван",
  "lastName": "Петров",
  "office": "Moscow"
}
```

**Response 201** (algorithm 0 — first available SAM)
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

**Response 201** (algorithm 1 — `johdo` was taken)
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

**Response 409** — employeeID already exists
```json
{
  "success": false,
  "message": "A user with employeeID '123456789' already exists in Active Directory."
}
```

**Response 409** — all sAMAccountName candidates taken
```json
{
  "success": false,
  "message": "All generated sAMAccountName candidates for 'John Doe' are already taken. Please create the account manually."
}
```

---

### PUT /api/users — Update user

Locate user by `employeeId`. Only non-null fields are written.

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

**Rename (only when `UpdateDisplayName: true` in appsettings)**
```json
{
  "employeeId": "123456789",
  "firstName": "Jonathan",
  "lastName": "Doe"
}
```

**Disable account (offboarding)**
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
    "employeeId": "123456789",
    "displayName": "John Doe",
    "email": "john.doe@company.com",
    "office": "NRW",
    "company": "ExampleCompany",
    "division": "New Division",
    "description": "Updated description",
    "isEnabled": true,
    "distinguishedName": "CN=John Doe,OU=Users,OU=NRW,DC=company,DC=local"
  }
}
```

**Response 404** — employeeID not found
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

### POST /api/exchange/mailbox/enable — Enable mailbox

**Request**
```json
{
  "samAccountName": "johdo"
}
```

**Response 200** (mailbox newly created)
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

**Response 200** (mailbox already existed — settings reapplied)
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

### POST /api/exchange/mailbox/disable — Disable mailbox

**Request**
```json
{
  "samAccountName": "johdo"
}
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

---

## Troubleshooting

| Symptom | Likely cause |
|---------|-------------|
| `Access is denied` on AD operations | Service account lacks permissions on the target OU, or wrong credentials in `appsettings.json` |
| `The server is not operational` | Wrong `Domain` value, or the machine is not domain-joined |
| `Object already exists` on create | Two users have identical `firstName`/`lastName` and all three SAM candidates + their corresponding CNs exist — create manually |
| Exchange cmdlet fails with `The term 'Enable-Mailbox' is not recognized` | WinRM not enabled on Exchange server, or wrong `ExchangePowerShellUri` |
| Exchange cmdlet fails with `Access Denied` | Service account not in the `Recipient Management` Exchange role |
| All sAMAccountName candidates taken (409) | All three algorithm slots are occupied — create the account manually and assign a unique SAM |
| Service fails to start | Check Windows Event Viewer → Application log; verify the published path in `sc.exe binpath=`; check that the `logs/` directory is writable |
| No console output when running `dotnet run` | Expected — ASP.NET Core infrastructure messages (`Microsoft.*`) are filtered to Warning level by Serilog. Startup messages from `Microsoft.Hosting.Lifetime` are still shown. Check the rolling log file in `logs/` for full output. |
