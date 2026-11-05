# Schermpostvak

Collects photos and videos sent to a WhatsApp number, lets a reviewer approve or reject them in a dashboard, and publishes approved items to Xibo screens.

```
WhatsApp chats ──> WAHA ──webhook──> backend (ASP.NET Core) ──> Xibo CMS ──> screens
                                                   │
                                          dashboard (Vue + Reka UI)
```

## Repository layout

| Path | What it is |
| --- | --- |
| `backend/Schermpostvak.Api` | ASP.NET Core (.NET 10) API, webhook, background worker, SQLite |
| `frontend` | Vue 3 + Vite + Reka UI dashboard (Dutch UI) |
| `docker-compose.yml` | Backend and WAHA (WhatsApp gateway) |
| `Dockerfile` | Builds the dashboard and backend into one image (with ffmpeg) |
| `Schermpostvak.slnx` | Solution file for Visual Studio / Rider / `dotnet` |

## How it works

1. **WAHA** is linked to a dedicated WhatsApp number (use its own SIM, never a personal number) and posts every message to `/webhook/waha`: incoming ones and those sent from the bot's own phone (event `message.any`). The webhook downloads photos and videos from WAHA right away, because WAHA deletes its copy after an hour.
2. The **webhook** registers each chat. New chats start **enabled**, so media is collected automatically; a reviewer can switch a chat off under *Chats* to stop collecting from it.
3. The **worker** (every 5 seconds) converts the media (photos to JPG without EXIF/GPS, videos to H.264 MP4), makes a thumbnail, skips duplicates and puts the item in the review queue.
4. A reviewer approves (show-from, time on screen, rotation) or rejects with a reason.
5. From show-from on, the worker uploads approved items to the **Xibo library** with the tag `onscreen` (setting `Xibo:Tag`). The layouts on the screens use a dynamic playlist that shows all media with that tag. Approved items stay on the screens until a reviewer removes them under *Live op schermen*. (Items approved with an end date by older versions still expire at that moment.)

## First-time setup

1. Copy `.env.example` to `.env` and fill in the secrets.
2. In Xibo, create an application (*Applications → Add application*, client credentials) and put its id and secret in `.env`.
3. In Xibo, make sure the layouts on the screens contain a dynamic playlist that filters on the media tag `onscreen`. Use another tag with the `XIBO_TAG` environment variable.
4. Start everything:
   ```bash
   docker compose up -d --build
   ```
5. Open the WAHA dashboard at http://localhost:3000/dashboard (log in with `WAHA_DASHBOARD_USERNAME` / `WAHA_DASHBOARD_PASSWORD`), add your API key as the server's key, start the session `default` and scan the QR code with the bot's phone (WhatsApp → Settings → Linked devices).
6. Open the dashboard at http://localhost:8080. Photos and videos from every chat land in the review queue; switch off any chat under *Chats* that you want to ignore.

## Local development

```bash
# terminal 1: WhatsApp gateway only
docker compose up -d waha

# terminal 2: backend on http://localhost:5080 (needs ffmpeg installed: brew install ffmpeg)
cd backend/Schermpostvak.Api
dotnet user-secrets init
dotnet user-secrets set "Webhook:Token" "..."
dotnet user-secrets set "Waha:ApiKey" "..."
dotnet user-secrets set "Xibo:BaseUrl" "https://..."
dotnet user-secrets set "Xibo:ClientId" "..."
dotnet user-secrets set "Xibo:ClientSecret" "..."
dotnet run

# terminal 3: dashboard on http://localhost:5173 (proxies /api to the backend)
cd frontend
npm install
npm run dev
```

When the backend runs outside Docker, point the WAHA webhook at your machine, e.g. `WHATSAPP_HOOK_URL=http://host.docker.internal:5080/webhook/waha?token=...`, and set `Waha:BaseUrl` to `http://localhost:3000`.

## Releasing

Pushing a version tag builds the dashboard and backend into one Docker image and publishes it to GitHub Container Registry (`.github/workflows/release.yml`):

```bash
git tag v1.0.0
git push origin v1.0.0
```

The image is tagged `1.0.0`, `1.0` and `latest` as `ghcr.io/97067883ludo/xibo-whatsapp-bot`. To run it on a server, replace `build: .` in `docker-compose.yml` with `image: ghcr.io/97067883ludo/xibo-whatsapp-bot:1.0.0`.

## Before going live

- **Login.** The dashboard has no authentication yet (reviewer is stored as "Beoordelaar"). Add Microsoft Entra ID login (`Microsoft.Identity.Web`) or put it behind your company's reverse proxy with SSO before exposing it.
- **Migrations.** The database is created with `EnsureCreated`. Switch to EF Core migrations before the schema changes.
- **Verify API versions.** WAHA and Xibo endpoints vary per version. Check `Services/WahaClient.cs` against WAHA's Swagger UI (http://localhost:3000) and `Services/XiboClient.cs` against your CMS's `/swagger.json` (especially `PUT /library/{id}`, used to set the tag, duration and expiry).
- **Time zone.** Show-from dates are stored as entered and sent to Xibo as-is, so the server and CMS should use the same time zone.
- **Privacy.** Tell everyone in the enabled chats that what they send may be shown on the screens, and how to ask for removal.
