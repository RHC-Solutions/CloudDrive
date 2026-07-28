# OAuth setup — OneDrive and Google Drive

OneDrive and Google Drive authenticate with OAuth 2, which needs an **app registration**. Every other
provider takes a key or a password and needs none of this.

CloudDrive ships the code but **not the registrations** — `OAuthProviders.BundledClientIds` is empty in
source. A client ID is not a secret, but committing one ties every build to a single registration that
one person has to keep verified, and a tenant that blocks third-party applications needs its own
anyway. So: register once, configure once, and sign-in works for everybody on the machine.

## How CloudDrive finds a client ID

Three places, in order of precedence:

| Where | Set by | Use when |
|---|---|---|
| The account's **Advanced** section | a user, in the account dialog | one account needs a different registration |
| `%ProgramData%\CloudDrive\oauth-clients.json` | an administrator, once per machine | **normal case** — the whole machine shares one |
| `OAuthProviders.BundledClientIds` | compiled in | you are building your own CloudDrive |

Until one of them has a value, the **Sign in** button explains what to register and offers to open the
right page. It does not fail silently.

---

## Microsoft (OneDrive and SharePoint)

1. Go to [Entra ID → App registrations](https://entra.microsoft.com/#view/Microsoft_AAD_RegisteredApps/ApplicationsListBlade)
   → **New registration**.
2. **Name**: anything — `CloudDrive`.
3. **Supported account types**: *Accounts in any organizational directory and personal Microsoft
   accounts*. This is what makes one registration work for both a work OneDrive and a personal one.
4. **Redirect URI**: platform **Mobile and desktop applications**, URI `http://localhost`.

   Not `https`, and no port. Entra treats `http://localhost` as a special case for desktop clients and
   accepts a callback on **any** port, which is what lets CloudDrive pick a free one at sign-in time
   instead of fighting whatever else is listening.
5. Register, then copy the **Application (client) ID**.
6. Under **Authentication**, confirm *Allow public client flows* is **Yes**. An installed app cannot
   keep a secret, so it authenticates with PKCE alone.
7. **Do not create a client secret.** A public client that sends one is rejected.

Permissions are requested at sign-in and consented to by the user, so nothing needs pre-granting for a
single user. CloudDrive asks for:

```
offline_access openid profile User.Read Files.ReadWrite.All Sites.ReadWrite.All
```

`offline_access` is the one that matters most — it is what returns a refresh token, and without it the
mount would stop working an hour after sign-in with no way to revive it unattended.

> **Tenant admin note.** `Files.ReadWrite.All` is not admin-restricted for a user's own files, but many
> tenants switch off user consent entirely. If sign-in fails with *"Need admin approval"*, a Global
> Administrator must press **Grant admin consent** on the registration once.

---

## Google Drive

1. Go to [Google Cloud → Credentials](https://console.cloud.google.com/apis/credentials), selecting or
   creating a project.
2. Enable the **Google Drive API** under *APIs & Services → Library*.
3. Configure the **OAuth consent screen**. Add the scope
   `https://www.googleapis.com/auth/drive`.
4. **Create credentials → OAuth client ID → Desktop app.**
5. Copy the **Client ID** *and* the **Client secret**.

   Google issues a secret even to desktop clients, and its token endpoint rejects the exchange without
   one. It is not a secret in any meaningful sense — it ships inside every copy of every installed app
   that uses it — which is exactly why CloudDrive keeps it in `oauth-clients.json` rather than in the
   encrypted credential store. Storing it as though it were protected would be theatre.

> **The 7-day trap.** While the consent screen is in **Testing**, Google expires refresh tokens after
> **7 days**. A mount configured on Monday stops on the following Monday and needs an interactive
> sign-in to recover. Set the publishing status to **In production** to get non-expiring tokens. For an
> internal Workspace app, choose **Internal** user type instead and the limit does not apply.

CloudDrive requests the **full** `drive` scope rather than `drive.file`, because `drive.file` only ever
sees files the application itself created — which for a drive mount means an empty folder.

---

## Configuring the machine

Create `%ProgramData%\CloudDrive\oauth-clients.json`:

```json
{
  "MicrosoftClientId": "00000000-1111-2222-3333-444444444444",
  "MicrosoftTenant": "common",
  "GoogleClientId": "123456789012-abcdefghijklmnop.apps.googleusercontent.com",
  "GoogleClientSecret": "GOCSPX-xxxxxxxxxxxxxxxxxxxx"
}
```

Omit any provider you are not using. `MicrosoftTenant` defaults to `common`, which accepts both
personal and work accounts; set a tenant GUID to restrict sign-in to one organisation.

That directory is ACL'd to SYSTEM and Administrators, so writing the file needs an elevated editor.

---

## What signing in actually does

```
tray app (your session)                    service (LocalSystem)
─────────────────────────                  ──────────────────────
1. generate PKCE verifier
2. open the system browser
3. you sign in and consent
4. code arrives on 127.0.0.1:<port>
5. exchange code + verifier
   → refresh token
6. send it over the pipe  ───────────────►  7. store, DPAPI under SYSTEM
                                            8. exchange refresh → access
                                               token before every mount
                                               ── no browser, no session ──
```

The interactive half runs **once**, in your session, because it needs a browser. Everything after that
is a refresh-token exchange, which needs neither — and that is precisely what lets a OneDrive mount
exist before anyone has signed in to Windows.

The system browser is used rather than an embedded WebView, per
[RFC 8252](https://datatracker.ietf.org/doc/html/rfc8252): an embedded browser hides which site you are
typing a password into, defeats the browser's own phishing protection, and cannot reuse an existing
session or a passkey. It also means CloudDrive never sees your password.

## When a sign-in expires

The service tells the difference between a grant that is **gone** and a network that is merely **down**.

A `invalid_grant` — revoked access, a changed password, a Google testing-mode token past 7 days — is
terminal. CloudDrive records it on the account, badges it in the UI, and raises a **ReauthRequired**
alert through Telegram, Slack or email. It does not keep retrying, because no amount of retrying fixes
it. Anything else is treated as transient and retried on the next reconcile.

Re-authorising is the same **Sign in** button; the existing mappings keep working afterwards without
being touched.

## Current limits

| | Drive letter / folder mount | Files On-Demand |
|---|---|---|
| OneDrive | ✅ | not yet |
| Google Drive | ✅ | not yet |

Drive-letter mounts go through rclone, which speaks both APIs, so they work as soon as an account is
signed in. The Files On-Demand path needs a `GraphStorageClient` and a `GoogleDriveStorageClient`
implementing `IRemoteStorageClient`; until those exist, the mapping dialog offers only the drive modes
for these two providers and `RemoteStorageClientFactory` says so explicitly rather than failing
obscurely.
