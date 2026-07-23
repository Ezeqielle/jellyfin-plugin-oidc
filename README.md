# Jellyfin OIDC RBAC Plugin

A Jellyfin plugin providing **OpenID Connect authentication** with **role-based library access control**.

Authenticate users via any OIDC-compatible identity provider (Authentik, Keycloak, Azure AD, Okta, etc.) and automatically assign Jellyfin permissions and library access based on IdP group/role claims.

## Features

- **OIDC Authentication** with PKCE (Authorization Code flow)
- **Multi-provider support** - configure multiple IdPs simultaneously with branded login buttons
- **Role-based access control** - map IdP roles/groups to Jellyfin permissions and specific libraries
- **Auto-provisioning** - create Jellyfin users on first SSO login
- **Flexible claim parsing** - extract roles from nested JWT claims (e.g. `realm_access.roles`, `groups`)
- **Merge semantics** - users with multiple roles get the union of all permissions (most permissive wins)
- **Default role fallback** - assign a baseline role to users with no matching IdP roles
- **Admin UI** - full configuration from the Jellyfin dashboard (Providers, Role Mappings, General settings)
- **Auto-injected login buttons** - no manual branding HTML required
- **Native/mobile app login** - sign in Android, iOS and TV apps via Jellyfin [Quick Connect](#mobile--native-apps-quick-connect)

## Installation

### Quick install — add this repository to Jellyfin

```
https://raw.githubusercontent.com/Ezeqielle/jellyfin-plugin-oidc/main/manifest.json
```

### From the Jellyfin Plugin Catalog

1. Go to **Admin Dashboard > Plugins > Repositories**
2. Click **Add repository** and paste the URL above (Repository Name: `OIDC RBAC`)
3. Go to **Catalog > Authentication**
4. Install **OIDC RBAC**
5. Restart Jellyfin

### Manual Installation

```bash
# Build via Docker (no .NET SDK needed)
make docker-build

# Copy to Jellyfin plugin directory
sudo cp dist/*.dll dist/meta.json /var/lib/jellyfin/plugins/OIDC-RBAC/

# Restart Jellyfin
sudo systemctl restart jellyfin
```

## Quick Start

### 1. Configure a Provider

Go to **Admin Dashboard > Plugins > OIDC RBAC > Providers tab**

| Field              | Example (Authentik)                                        |
|--------------------|------------------------------------------------------------|
| Provider ID        | `authentik`                                                |
| Display Name       | `Authentik`                                                |
| Authority URL      | `https://auth.example.com/application/o/jellyfin/`         |
| Client ID          | *(from your IdP)*                                          |
| Client Secret      | *(from your IdP)*                                          |
| Scopes             | `openid profile email groups`                              |
| Role Claim Path    | `groups`                                                   |
| Username Claim     | `preferred_username`                                       |
| Picture Claim      | `picture`                                                  |
| Sync profile image | *(checkbox, on by default)*                                |
| Server Base URL    | *(optional, e.g. `https://jellyfin.example.com`)*          |

> **Server Base URL** is only needed if Jellyfin can't resolve its public URL on its own (e.g. behind a reverse proxy whose `X-Forwarded-*` headers aren't trusted). See [Reverse proxy / redirect_uri](#reverse-proxy--redirect_uri).

You may also need to put the appropriate role path in the scope. See [Supported Claim Paths](#supported-claim-paths).

### Profile image sync

When **Sync profile image** is enabled, on every login the plugin reads the **Picture Claim**
(default `picture`, the standard OIDC avatar claim) and sets it as the user's Jellyfin avatar,
overwriting any existing one. It looks in the ID token, then the access token, then the
provider's **userinfo** endpoint. Failures never block login. Leave the claim blank or uncheck
the box to disable it for a provider.

> The provider must actually emit the claim. Many IdPs do not include `picture` by default:
> - **Authentik** — its default `profile` scope omits `picture`. Add a Scope Mapping and attach
>   it to the provider — see [Authentik: Profile Picture / Avatar](examples/authentik/SETUP.md#25-optional-profile-picture--avatar).
> - **Keycloak** — add a "User Attribute"/hardcoded mapper that puts a `picture` claim in the
>   ID token or userinfo.
> - **Google** — includes `picture` in the ID token by default.

### 2. Create Role Mappings

Go to **Role Mappings tab** and create mappings:

**Example - Admin role:**
- Role Name: `jellyfin-admins`
- Administrator: checked
- All Libraries: checked

**Example - Standard user:**
- Role Name: `jellyfin-users`
- Libraries: select specific libraries
- Playback, Remote Access, Transcoding: checked

**Example - Kids:**
- Role Name: `jellyfin-kids`
- Libraries: Kids only
- Max Parental Rating: 7

### 3. Add the Login Button

Go to **Admin Dashboard > General > Branding > Login disclaimer** and paste:

```html
<a href="/sso/OIDC/Start/authentik"
   class="raised block emby-button button-submit"
   style="display:block;margin:1em 0;padding:.9em;text-align:center;text-decoration:none;">
  Sign in with Authentik
</a>
```

> The auto-injected buttons (`/sso/OIDC/LoginButtons`) and the SSO flow automatically honor a
> Jellyfin **base URL** (Networking > Base URL). If you hand-write the `<a>` snippet above and run
> Jellyfin under a base path, prefix the href yourself, e.g. `href="/base_url/sso/OIDC/Start/authentik"`.

## Migrating Existing Users

Already have Jellyfin users you want to move to SSO without losing watch history? See [MIGRATION.md](MIGRATION.md) — username-match is automatic, but there are a few caveats around permissions overwrite and password fallback.
Also it is recommendable that you create a backup-admin user with password login, for two reasons:
* If your provider is down, you can still log in
* If you mess up the roles, your main administrator account will get downgraded to a normal user. If this happens, try this [fix](https://jellyfin.org/docs/general/administration/troubleshooting/#unlock-locked-user-account).

## How It Works

```
Browser                    Jellyfin Plugin              Identity Provider
   |                            |                            |
   |--- Click SSO button ------>|                            |
   |                            |--- OIDC authorize -------->|
   |<---------------------------|    (with PKCE)             |
   |                            |                            |
   |--- Login at IdP -----------|--------------------------->|
   |<---------------------------|------- callback + code ----|
   |                            |                            |
   |                            |--- exchange code --------->|
   |                            |<------ ID token + roles ---|
   |                            |                            |
   |                            |--- sync user + RBAC        |
   |                            |--- issue Jellyfin session  |
   |<--- authenticated ---------|                            |
```

1. User clicks the SSO login button on the Jellyfin login page
2. Plugin redirects to the IdP's authorization endpoint (with PKCE)
3. User authenticates at the IdP
4. IdP redirects back with an authorization code
5. Plugin exchanges the code for tokens, extracts roles from the configured claim path
6. Plugin creates/updates the Jellyfin user and applies role-based permissions
7. Plugin issues a Jellyfin session token and redirects to the dashboard

## Mobile & native apps (Quick Connect)

**The SSO login button only works in the browser-based Jellyfin Web client.** The button is
injected into the web login page and the flow finishes by writing credentials into the browser's
`localStorage`. Native apps (Android, iOS/Swiftfin, Android TV, etc.) render their own login
screen and keep credentials in native storage, so they never see the button and can't consume that
web session. This is a limitation of how Jellyfin exposes login to plugins, not a bug.

To sign a native app in via SSO, the plugin bridges to Jellyfin's built-in **Quick Connect**:

```
Native app                 Browser                    Jellyfin Plugin           Identity Provider
   |                          |                            |                          |
   |-- tap Quick Connect      |                            |                          |
   |   (shows 6-digit code)   |                            |                          |
   |   ...polling...          |                            |                          |
   |                          |-- open QuickConnect link ->|-- OIDC authorize ------->|
   |                          |<-- login at IdP -----------|<------ callback + code --|
   |                          |                            |-- sync user + RBAC       |
   |                          |-- enter 6-digit code ----->|-- AuthorizeRequest ----->|
   |<-- authenticated, signed in --------------------------|                          |
```

**Setup:**

1. Enable **Quick Connect** in Jellyfin: *Admin Dashboard > General > Quick Connect > Enable*.
2. On the mobile/native app, open the login screen and choose **Quick Connect** — it shows a
   6-digit code and starts polling.
3. In any browser (on the phone or another device), open
   `https://jellyfin.example.com/sso/OIDC/QuickConnect/<providerId>`
   (the injected login page also shows a small *"Sign in a device … (Quick Connect)"* link for this).
4. Authenticate at your IdP as usual.
5. Enter the 6-digit code from step 2 and click **Authorize**.
6. The native app's poll completes and it signs in.

> Quick Connect codes are short-lived. Start the flow on the app first, then enter the code
> promptly. A mistyped code can be re-entered without repeating the IdP login.

## RBAC Details

### Role Merging

When a user matches multiple role mappings, permissions are **merged (union)**:
- Boolean permissions: `true` if **any** matched role has it enabled
- Libraries: union of all matched roles' library sets
- `EnableAllLibraries`: `true` if any role enables it
- `MaxParentalRating`: highest value across all matched roles

### Priority

Each role mapping has a priority field. Higher priority roles take precedence in ordering, though merge semantics still apply.

### Default Role

If no role mappings match a user's IdP roles, the **Default Role** (configured in the General tab) is used as a fallback.

### Supported Claim Paths

The **Role Claim Path** supports:

| Path                   | Token Structure                                  | Provider     |
|------------------------|--------------------------------------------------|--------------|
| `groups`               | `{"groups": ["admin", "users"]}`                 | Authentik    |
| `realm_access.roles`   | `{"realm_access": {"roles": ["admin"]}}`         | Keycloak     |
| `roles`                | `{"roles": ["admin"]}`                           | Custom/Azure |

The plugin checks both the ID token and access token for role claims.

## Reverse proxy / redirect_uri

The plugin builds the OIDC `redirect_uri` from Jellyfin's published URL via `IServerApplicationHost.GetSmartApiUrl()`. This honors Jellyfin's **Published Server URLs** field (Admin Dashboard > Networking) and any trusted `X-Forwarded-*` headers from a proxy listed under **Known proxies**.

If your IdP rejects the callback with `Invalid redirect_uri` (or you see `127.0.0.1:8096` in the URL), pick one of these:

- **Recommended:** set **Published Server URL** in Jellyfin > Networking and/or add your proxy to **Known proxies** so Jellyfin trusts the forwarded host.
- **Or:** set the per-provider **Server Base URL** field to the exact origin your IdP has registered (e.g. `https://jellyfin.example.com`). It overrides auto-detection.

The path is always appended as `/sso/OIDC/Callback/{providerId}`, so make sure the IdP's allowed redirect URI matches that suffix.

## Identity Provider Guides

### Authentik

See [examples/authentik/SETUP.md](examples/authentik/SETUP.md) for a complete step-by-step guide including:
- Docker Compose stack (Jellyfin + Authentik)
- Group creation and OIDC provider configuration
- Custom property mapping for filtered role claims
- Troubleshooting

### Keycloak

1. Create a new Client in your realm (Client type: OpenID Connect, Client authentication: On)
2. Set Valid Redirect URIs: `https://jellyfin.example.com/sso/OIDC/Callback/keycloak`
3. Roles are in `realm_access.roles` by default
4. Plugin config: Authority = `https://keycloak.example.com/realms/myrealm`, Role Claim Path = `realm_access.roles`

## API Endpoints

| Method | Endpoint                          | Description                        |
|--------|-----------------------------------|------------------------------------|
| GET    | `/sso/OIDC/Start/{providerId}`    | Initiate OIDC flow (web client)    |
| GET    | `/sso/OIDC/Callback/{providerId}` | OIDC callback (handles code exchange) |
| POST   | `/sso/OIDC/Auth/{providerId}`     | Complete authentication (web client) |
| GET    | `/sso/OIDC/QuickConnect/{providerId}` | Initiate OIDC flow for a native app via Quick Connect |
| POST   | `/sso/OIDC/QuickConnect/Authorize/{providerId}` | Authorize a Quick Connect code after OIDC login |
| GET    | `/sso/OIDC/Providers`             | List enabled providers             |
| GET    | `/sso/OIDC/LoginButtons`          | JS snippet for login buttons       |
| GET    | `/sso/OIDC/BrandingSnippet`       | HTML snippet for branding config   |
| GET    | `/sso/OIDC/Config/Libraries`      | List available libraries (admin)   |
| GET    | `/sso/OIDC/Config/Status`         | Plugin status (admin)              |

## Building

### Requirements

- .NET 9.0 SDK **or** Docker

### Build

```bash
# With .NET SDK
make build

# With Docker only
make docker-build
```

### Package (installable zip)

```bash
make package
# Output: dist/oidc-rbac.zip
```

### Release

```bash
git tag v1.0.0
git push origin v1.0.0
# GitHub Actions builds, creates a release, and updates manifest.json
```

## Project Structure

```
Jellyfin.Plugin.OIDC/
  OidcPlugin.cs                  # Plugin entry point
  Configuration/
    PluginConfiguration.cs       # Provider + role mapping config DTOs
    configPage.html              # Admin UI (embedded resource)
  Api/
    OidcController.cs            # OIDC authorization code flow
    ConfigController.cs          # Admin config API
    LoginButtonController.cs     # Auto-injected login buttons
  Auth/
    OidcAuthProvider.cs          # Blocks password login for SSO users
  Services/
    StateManager.cs              # Thread-safe OIDC state with TTL
    ClaimParser.cs               # JWT claim extraction (nested paths)
    RbacService.cs               # Role-to-permission mapping engine
    UserSyncService.cs           # User provisioning and sync
    ServiceRegistrator.cs        # DI registration
```

## License

GPLv3 (required by linking against Jellyfin's GPLv3 libraries)
