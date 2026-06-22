# Hosting the FatGuysSpeak server on Azure

This is the step-by-step for running the server on **Azure App Service (Linux)**, the recommended
single-instance host for this app. A Dockerfile is also included for Azure Container Apps / a VM.

## The one hard constraint

The SignalR hub keeps all live state (online users, voice-channel membership, active streamers) in
**static in-memory dictionaries with no distributed backplane**. The server therefore runs as a
**single instance** and must **not be scaled out**. Keep the App Service plan at 1 instance and scale
*up* (bigger plan) rather than *out*. Do **not** add Azure SignalR Service — it doesn't share the
app's static state and would only add cost.

## What the code already handles

- **Database**: reads the `DATABASE_URL` env var as a `postgres://user:pass@host:5432/db` URI and
  connects with SSL required. With no `DATABASE_URL` it falls back to SQLite on disk.
- **Behind a proxy**: trusts `X-Forwarded-*` when `DATABASE_URL` is set, when `WEBSITE_SITE_NAME` is
  set (App Service always sets this), or when `TRUST_FORWARDED_HEADERS=1`. So per-IP rate limiting
  sees real client IPs on Azure automatically — no extra config.
- **Uploads**: written under the content root. On App Service for Linux that's `/home`, which is
  persistent across restarts and deploys, so uploaded files and a SQLite DB survive. (On Container
  Apps the filesystem is ephemeral — mount Azure Files there instead.)
- **TLS / port**: App Service terminates TLS and sets the listen port; the app honours it. No change.

## Required vs optional settings

- **Required**: `Jwt__Key` — a long random secret (the app throws at startup if unset). Issuer and
  audience have committed defaults in `appsettings.json`; override with `Jwt__Issuer` / `Jwt__Audience`
  only if you want to change them.
- **Optional (features)**: `Anthropic__ApiKey`, `Anthropic__Model` (PorkChop), `ElevenLabs__ApiKey`,
  `ElevenLabs__VoiceIds__0`, `ElevenLabs__VoiceIds__1`, … (spoken roasts/TTS). PorkChop and TTS simply
  stay off if these are absent.
- **Optional (database)**: `DATABASE_URL` to use Postgres instead of SQLite.

## Stand it up (Azure CLI)

```bash
az login

RG=fatguysspeak-rg
APP=fatguysspeak-server          # must be globally unique; becomes <APP>.azurewebsites.net
LOCATION=eastus                  # pick the region nearest your testers (voice latency)

az group create -n $RG -l $LOCATION

# B1 Linux plan (~$13/mo). Bump --sku to B2/B3 if voice/screen-share feels constrained.
az appservice plan create -g $RG -n fatguysspeak-plan --is-linux --sku B1

az webapp create -g $RG -p fatguysspeak-plan -n $APP --runtime "DOTNETCORE:9.0"

# Keep the single instance warm, allow WebSockets, force HTTPS.
az webapp config set -g $RG -n $APP --web-sockets-enabled true --always-on true
az webapp update     -g $RG -n $APP --https-only true

# Secrets / feature keys (add Anthropic/ElevenLabs only if you want PorkChop + TTS).
az webapp config appsettings set -g $RG -n $APP --settings \
  Jwt__Key="REPLACE_WITH_A_LONG_RANDOM_SECRET" \
  Anthropic__ApiKey="sk-ant-..." \
  Anthropic__Model="claude-sonnet-4-6" \
  ElevenLabs__ApiKey="sk_..." \
  ElevenLabs__VoiceIds__0="<voiceId1>" \
  ElevenLabs__VoiceIds__1="<voiceId2>"
```

Using **Postgres** instead of SQLite? Create it and add `DATABASE_URL`:

```bash
az postgres flexible-server create -g $RG -n fatguysspeak-db \
  -l $LOCATION --tier Burstable --sku-name Standard_B1ms \
  --storage-size 32 --version 16 --admin-user fgsadmin --admin-password "<STRONG_PW>"
# then:
az webapp config appsettings set -g $RG -n $APP --settings \
  DATABASE_URL="postgres://fgsadmin:<STRONG_PW>@fatguysspeak-db.postgres.database.azure.com:5432/postgres"
```

## Deploy the code

Two options — both build the headless `net9.0` target:

1. **GitHub Actions (included)** — set `AZURE_WEBAPP_NAME` in `.github/workflows/azure-deploy.yml` to
   `$APP`, then add the publish profile as a repo secret named `AZURE_WEBAPP_PUBLISH_PROFILE`:
   ```bash
   az webapp deployment list-publishing-profiles -g $RG -n $APP --xml
   ```
   Copy that XML into the secret, then run the **Deploy server to Azure App Service** workflow from the
   Actions tab (manual trigger — it never auto-deploys on a push).

2. **One-shot from your machine**:
   ```bash
   dotnet publish FatGuysSpeak.Server -c Release -f net9.0 -o publish
   cd publish && zip -r ../app.zip . && cd ..
   az webapp deploy -g $RG -n $APP --src-path app.zip --type zip
   ```

## Custom domain + free TLS cert

```bash
# 1) At Porkbun, add a CNAME: chat.fatguysspeak.com -> <APP>.azurewebsites.net
az webapp config hostname add -g $RG --webapp-name $APP --hostname chat.fatguysspeak.com
# 2) Free managed certificate, then bind it (SNI):
az webapp config ssl create -g $RG --name $APP --hostname chat.fatguysspeak.com
az webapp config ssl bind   -g $RG --name $APP \
  --certificate-thumbprint <thumbprint-from-previous-command> --ssl-type SNI
```

## Point the client at the server

The client defaults its server URL to `http://localhost:5238` and persists whatever the user enters in
Settings. For the beta, set the default to your Azure host so testers don't have to type it: edit
`ApiService.DefaultServerUrl` in `FatGuysSpeak.Client/Services/ApiService.cs` to
`https://chat.fatguysspeak.com` and rebuild the client installer. (Left unchanged for now since the
hostname isn't decided yet.)

## Rough monthly cost

- App Service **B1** Linux + SQLite on the persistent disk: **~$13** (plus egress; first 100 GB/mo free).
- App Service **B2/B3** for more voice headroom: ~$26 / ~$52.
- Add managed **Postgres** (Burstable B1ms + 32 GB): **~$15–20** on top.
- A new Azure account includes **$200 credit for 30 days**, so the beta can start at $0.

Screen-share/webcam are bandwidth-heavy (~1–2 Mbps per viewer); heavy use can push past the free
100 GB/mo egress at ~$0.08/GB after.

## Dashboard credentials & rotation

The admin dashboard (`/dashboard`, login at `/dashboard/login`) authenticates against the
`Dashboard__Username` / `Dashboard__Password` app settings. The dashboard's admin actions are no
longer restricted to localhost — they're protected solely by this login — so the password must be
strong and unique. **Never commit the password value to the repo.**

Rotate the password (generates a fresh value, sets it, and restarts the app):

```powershell
$bytes = New-Object byte[] 28
[System.Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
$chars = 'ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789'
$pw = -join ($bytes | ForEach-Object { $chars[$_ % $chars.Length] })
az webapp config appsettings set -g fatguysspeak-rg -n fatguysspeak-server `
  --settings "Dashboard__Password=$pw" -o none   # -o none avoids dumping other secrets
"New password: $pw"   # store in a password manager; not retrievable later
```

Changing any app setting restarts the web app (brief 502, then back). Verify with `/api/version`
(200) and `/dashboard/login` (200), then log in to confirm.

Rotation log:
- 2026-06-22 — dashboard password rotated to a fresh 28-char random value (after the admin endpoints
  were opened to remote access).
