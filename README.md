# LDAP API — Active Directory & Exchange Management

A .NET 10 Minimal API designed to run as a **Windows Service** on a domain-joined Windows machine. It provides a REST interface for provisioning and managing Active Directory user accounts and Exchange mailboxes.

---

## What it does

| Operation | Description |
|-----------|-------------|
| **Create AD user** | Generates a unique `sAMAccountName`, generates a random password, creates the account in a configurable OU, sets all required attributes |
| **Update AD user** | Finds user by `employeeID`, updates any supplied attributes; optionally updates display name |
| **Disable AD user** | Triggered by the update endpoint with `userAccountControl: "disabled"` — accounts are **never deleted** |
| **Enable mailbox** | Checks for an existing mailbox, creates it if absent, unhides it from address lists, enables ActiveSync + OWA |
| **Disable mailbox** | Runs `Disable-Mailbox` against the Exchange PowerShell endpoint |

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

### sAMAccountName generation logic

| Attempt | Formula | Example (John Doe) |
|---------|---------|-------------------|
| 1st | first **3** letters of firstName + first **2** letters of lastName | `johdo` |
| 2nd | first **2** letters of firstName + first **3** letters of lastName | `jodoe` |
| 3rd | first **3** letters of firstName + first **3** letters of lastName | `johdoe` |

Names are lowercased and stripped of diacritics and special characters before slicing.
If all three candidates are taken, the API returns `409 Conflict` and the account must be created manually.

### Password generation

12-character random password containing at least one uppercase letter, one lowercase letter, one digit, and one special character. Visually ambiguous characters (I, O, 0, 1, l) are excluded.

### ServiceAccountUsername / ServiceAccountPassword — clarification

These credentials are used to:
1. Bind to Active Directory (`PrincipalContext` and `DirectoryEntry` / `DirectorySearcher`)
2. Authenticate against the Exchange PowerShell remoting endpoint

They are **not** the Windows service logon account. That is configured separately via Services Manager or `sc.exe` at installation time. Leave both fields empty if the Windows service identity already has the necessary AD and Exchange permissions (recommended for production).

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
  "AdSettings": {
    "Domain": "company.local",
    "EmailDomain": "company.com",
    "DefaultUserOu": "OU=Users,DC=company,DC=local",
    "ServiceAccountUsername": "COMPANY\\svc-ldapapi",
    "ServiceAccountPassword": "s3cr3t",
    "ExchangePowerShellUri": "http://exchange-server.company.local/PowerShell",
    "UpdateDisplayName": true
  }
}
```

| Setting | Description |
|---------|-------------|
| `Domain` | Internal AD domain (used for `PrincipalContext` and `userPrincipalName`) |
| `EmailDomain` | Mail domain for the generated `mail` attribute (often differs from AD domain) |
| `DefaultUserOu` | OU where new accounts are created; can be overridden per-request via `targetOu` |
| `ServiceAccountUsername` | LDAP bind account. Leave empty to use the Windows service identity |
| `ServiceAccountPassword` | Password for the above account |
| `ExchangePowerShellUri` | WS-Man endpoint of Exchange Management Shell |
| `UpdateDisplayName` | `true` = allow updating GivenName/Surname/DisplayName on update calls |

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
  "office": "Moscow",
  "company": "ExampleCompany",
  "division": "Division",
  "description": "Test account",
  "targetOu": null
}
```

**Response 201**
```json
{
  "success": true,
  "message": null,
  "data": {
    "status": "created",
    "employeeId": "123456789",
    "samAccountName": "johdo",
    "email": "johdo@company.com",
    "password": "Kp3!mNzR7@qW"
  }
}
```

**Response 409** (all sAMAccountName candidates taken)
```json
{
  "success": false,
  "message": "All generated sAMAccountName candidates for 'John Doe' are already taken."
}
```

---

### PUT /api/users — Update user

Locate user by `employeeId`. Only non-null fields are written.
Set `userAccountControl` to `"disabled"` to disable the account.

**Update attributes**
```json
{
  "employeeId": "123456789",
  "firstName": "John",
  "lastName": "Doe",
  "office": "Saint Petersburg",
  "company": "ExampleCompany",
  "division": "New Division",
  "description": "Updated description",
  "userAccountControl": null
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
    "email": "johdo@company.com",
    "office": "Saint Petersburg",
    "company": "ExampleCompany",
    "division": "New Division",
    "description": "Updated description",
    "isEnabled": true,
    "distinguishedName": "CN=John Doe,OU=Users,DC=company,DC=local"
  }
}
```

---

### GET /api/users/{samAccountName} — Get user

```
GET http://localhost:5000/api/users/johdo
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

**Response 200** (mailbox already existed)
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
| Exchange cmdlet fails with `The term 'Enable-Mailbox' is not recognized` | WinRM not enabled on Exchange server, or wrong `ExchangePowerShellUri` |
| Exchange cmdlet fails with `Access Denied` | Service account not in the `Recipient Management` Exchange role |
| All sAMAccountName candidates taken | Generate the account name manually and use a direct AD tool |
| Service fails to start | Check Windows Event Viewer → Application log; verify the published path in `sc.exe binpath=` |
