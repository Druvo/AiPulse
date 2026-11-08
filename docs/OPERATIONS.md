# Operating AiPulse

How AiPulse is actually run in production: one framework-dependent .NET 8 process under systemd, behind nginx
for TLS, with its data in `App_Data/` beside the binaries. The same steps apply to any Linux host. Host names below
are placeholders (`aipulse.example.com`, `deploy@your-server`).

For a quick look at a running instance, sign in as an admin and open **/ops**. It shows uptime, version, memory,
DB/App_Data/disk sizes, the last feed poll, failing sources, AI switches, backups and the audit log.

## 1. Server prerequisites

- Ubuntu 22.04 or 24.04 (any systemd distro works), with nginx and certbot.
- The ASP.NET Core 8 runtime: `sudo apt install -y aspnetcore-runtime-8.0`. Check with `dotnet --list-runtimes`.
- A non-root user that owns the app folder. This guide uses `aipulse`; any normal account works.
- DNS for your host name pointing at the server, and ports 80/443 open. The app's own port (5257) must **not** be
  reachable from outside (see section 3).

## 2. Build and copy

On the build machine (Windows or Linux):

```bash
dotnet publish AiPulse.csproj -c Release -r linux-x64 --self-contained false -o publish
```

The output includes `AiPulse.dll`, `appsettings.json`, `Data/` (the curated knowledge base) and `wwwroot/`. It
never contains `App_Data/` or the test project. Copy it to the server:

```bash
tar czf aipulse.tar.gz -C publish .
scp aipulse.tar.gz deploy@your-server:/tmp/
ssh deploy@your-server 'sudo mkdir -p /opt/aipulse && sudo tar xzf /tmp/aipulse.tar.gz -C /opt/aipulse && sudo chown -R aipulse:aipulse /opt/aipulse'
```

Production settings go in `/opt/aipulse/appsettings.Production.json`. That file is git-ignored and never part of a
publish, so it survives upgrades:

```json
{
  "AllowedHosts": "aipulse.example.com",
  "Auth": { "Username": "admin", "PasswordHash": "<PBKDF2 hash>", "Password": "", "RequireApproval": true },
  "Notifications": { "PollMinutes": 15 },
  "Ollama": { "BaseUrl": "http://<ollama-host>:11434" }
}
```

`Auth` only seeds the **first** admin when the Users table is empty. After that, accounts are managed on `/users`.
To get a hash, run the app once in Development and open `/auth/hash?password=…`. Alternatively, set a strong
`Auth__Password` for the first start only and change it in Settings afterwards. Keep secrets (SMTP password, LLM
API keys) in this file or in systemd `Environment=` lines, never in git.

## 3. systemd unit

`/etc/systemd/system/aipulse.service`:

```ini
[Unit]
Description=AiPulse
After=network.target

[Service]
WorkingDirectory=/opt/aipulse
ExecStart=/usr/bin/dotnet /opt/aipulse/AiPulse.dll
Restart=always
RestartSec=10
SyslogIdentifier=aipulse
User=aipulse
Environment=ASPNETCORE_ENVIRONMENT=Production
# Listen on loopback only: Program.cs trusts X-Forwarded-* from any address, which is safe only while nginx is the sole way in.
Environment=ASPNETCORE_URLS=http://127.0.0.1:5257
# Optional: shown on /ops when the build machine had no git metadata.
# Environment=AIPULSE_COMMIT=abc1234

[Install]
WantedBy=multi-user.target
```

```bash
sudo systemctl daemon-reload && sudo systemctl enable --now aipulse
sudo systemctl status aipulse
curl -s http://127.0.0.1:5257/health      # -> Healthy
```

`WorkingDirectory` matters. The content root is the working directory, so `App_Data/` (database, keys, history)
and the default `backups/` folder are created under `/opt/aipulse`. On the first start the app creates the
database, seeds sources from `Data/sources.json` and bootstraps the admin.

## 4. nginx

```nginx
server {
    listen 80;
    server_name aipulse.example.com;
    return 301 https://$host$request_uri;
}

server {
    listen 443 ssl http2;
    server_name aipulse.example.com;
    ssl_certificate     /etc/letsencrypt/live/aipulse.example.com/fullchain.pem;
    ssl_certificate_key /etc/letsencrypt/live/aipulse.example.com/privkey.pem;

    client_max_body_size 50M;            # backup restore and OPML uploads

    location / {
        proxy_pass http://127.0.0.1:5257;
        proxy_http_version 1.1;
        proxy_set_header Upgrade $http_upgrade;          # Blazor Server runs over a WebSocket
        proxy_set_header Connection "upgrade";
        proxy_set_header Host $host;
        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto $scheme;      # without it the app thinks it is on http (sitemap, OAuth callbacks)
        proxy_read_timeout 86400s;                       # long-lived circuits
        proxy_send_timeout 86400s;
    }
}
```

Get the certificate with `sudo certbot --nginx -d aipulse.example.com`, then run `sudo nginx -t && sudo systemctl
reload nginx`. Do not add `Cache-Control: immutable` for `.css`/`.js` at the proxy. The app sends `no-cache` on
purpose so an upgrade's new `app.css` is picked up immediately, and file names are not fingerprinted.

## 5. Upgrading

The automated pipeline (`Jenkinsfile`) runs these steps. Done by hand:

1. **Back up first**: use "Create backup now" on `/ops`, or `sudo tar czf /tmp/aipulse-$(date +%F).tar.gz -C /opt aipulse`.
2. `dotnet publish` as in section 2, then copy the output to a temporary folder on the server.
3. Copy it over `/opt/aipulse` (`sudo cp -r /tmp/aipulse-deploy/. /opt/aipulse/`). `App_Data/`, `backups/` and
   `appsettings.Production.json` are never part of a publish, so they are left alone.
4. `sudo systemctl restart aipulse`, then check `systemctl is-active aipulse` and `curl -s http://127.0.0.1:5257/health`.
5. Open `/ops` and confirm the version/commit and that the next poll completes.

Schema changes are applied automatically at startup (missing tables and columns are created). There are no
migrations to run, and no downgrade path other than restoring a backup.

**Caveat: curated content in `Data/`.** Approving a Glossary, Tools or Learning candidate appends to
`Data/glossary.json`, `Data/tools.json` or `Data/learning.json` in the install folder, not to App_Data. An upgrade
that copies a fresh `Data/` over the install therefore discards those approvals, and App_Data backups do not
contain them. Until that moves into the database, copy those three files aside before an upgrade and merge them
back afterwards (or keep the approved entries in the repository's `Data/` files).

## 6. Backups and restore

**What to back up:** `App_Data/` (everything that changes at runtime: the SQLite DB, data-protection keys, feed
history, per-user state, AI switches, caches) and `appsettings.Production.json`. A backup holds password hashes and
the keys that decrypt stored OAuth secrets, so keep it private (`chmod 600`, off-site storage you trust).

**Making one:**

- `/ops` → **Create backup now** writes `backups/aipulse-backup-YYYYMMDD-HHMMSS.zip` (or into
  `Ops:BackupDirectory`). It is listed with its size and can be downloaded from there. The folder sits outside
  `App_Data` so backups do not nest. Nothing is pruned automatically. Delete old zips yourself, e.g.
  `find /opt/aipulse/backups -name 'aipulse-backup-*.zip' -mtime +30 -delete`.
- Settings → Backup downloads the same zip directly (admins only).
- At the file level: `sudo systemctl stop aipulse && sudo tar czf aipulse-data.tgz -C /opt/aipulse App_Data && sudo systemctl start aipulse`.
  Stopping first gives a consistent SQLite copy. The in-app backup copies the database while it is open, which is
  fine for a quiet instance.

**Restoring:**

- *From the browser* (app running): Settings → Backup → upload the zip. Files in the zip overwrite the matching
  files in `App_Data`, and files not in the zip stay. Then `sudo systemctl restart aipulse`, because several services
  load their data only at startup.
- *On the server* (safest, and the only way when the app will not start):
  ```bash
  sudo systemctl stop aipulse
  sudo mv /opt/aipulse/App_Data /opt/aipulse/App_Data.before-restore
  sudo mkdir /opt/aipulse/App_Data
  sudo unzip /path/to/aipulse-backup-….zip -d /opt/aipulse/App_Data
  sudo chown -R aipulse:aipulse /opt/aipulse/App_Data
  sudo systemctl start aipulse && curl -s http://127.0.0.1:5257/health
  ```
  Delete `App_Data.before-restore` once you are happy.

Restoring `dp-keys/` together with the database keeps existing sessions and stored OAuth secrets valid. Restoring
the database without its keys signs everyone out and means re-entering the Google/GitHub/Clerk settings.

## 7. Monitoring

- `GET /health` is anonymous and returns `Healthy` (200) only when the process is up **and** SQLite answers. Point
  uptime monitors and load balancers here.
- `GET /ops/status.json` (admin session only) is the full snapshot behind `/ops`: process, poll, feed health, AI
  and backups. It is never public, because it exposes paths, versions and error text.
- Logs: `journalctl -u aipulse -f`. Feed failures are warnings (`Feed failed: <source>`). Unhandled request
  errors are logged as errors.

## 8. Troubleshooting

| Symptom | Likely cause and fix |
|---|---|
| 502 from nginx | The service is down or still starting. Run `systemctl status aipulse` and `journalctl -u aipulse -n 100`. A crash at startup is often a permissions problem on `App_Data` (`chown -R aipulse:aipulse /opt/aipulse`) or a malformed `appsettings.Production.json`. |
| Page loads, then "Reconnecting…" forever | WebSockets are not reaching Kestrel. Check the `Upgrade`/`Connection` headers and `proxy_read_timeout` in nginx. |
| Redirect loop, or links and sitemap show `http://` | nginx is not sending `X-Forwarded-Proto`. |
| HTTP 400 "Invalid Hostname" | `AllowedHosts` does not include the host name visitors use. |
| Everyone signed out after an upgrade or move | `App_Data/dp-keys` was lost or replaced. Restore it from a backup, or accept that users sign in again and re-enter the OAuth settings. |
| Admin locked out | Five wrong passwords lock an account for 15 minutes. Another admin can clear the lockout on `/users`. With a single admin, wait it out, or (with the service stopped) clear `LockoutUntil` on the row in `App_Data/aipulse.db` with `sqlite3`. |
| Forgot the only admin password | Another admin can use "Generate reset link" on `/users`. With a single admin, use `/forgot-password` if the account has an email (without SMTP the link is written to `journalctl -u aipulse`). Otherwise stop the service, generate a hash with a local Development run (`/auth/hash?password=…`) and write it to that user's `PasswordHash` in `App_Data/aipulse.db` with `sqlite3`. |
| Sources failing | Check `/ops` → Feed health, then `/source-health` for the error per source. Reddit answers bursts with 429, which is why only 4 subreddits are fetched per poll (`/ops` shows the rotation). Disable dead sources on `/sources`. |
| Disk filling up | `/ops` shows the sizes of App_Data, the DB and the backups. Prune `backups/`, and `feed-history.json` trims itself to 90 days. |
| AI features silent | `/ops` → AI features: check whether calls are forced off by `Ai:Enabled`, the master switch is off, or no provider is configured in `Llm:Providers`. |

## 9. Docker (alternative)

`docker compose up -d` builds the image from the `Dockerfile` (listening on :8080 as the non-root `app` user) and
keeps `App_Data` in the named volume `aipulse-data`. Put the same nginx config in front of it, pointing at
`127.0.0.1:8080`. Backups made from `/ops` go to `/app/backups` inside the container. Mount a second volume at `/app/backups` (or set
`Ops__BackupDirectory` to a mounted path) if they must survive a container rebuild.
