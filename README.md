# Facebook Group Content Distribution (Group Publisher)

A Blazor Server application that distributes text, images, videos and PDFs to Facebook groups the logged-in account
already belongs to, using Microsoft Playwright to drive a browser profile the user signs into manually.

> **Read this first.** Meta provides **no official API** for posting to groups (Groups API publishing was removed in
> April 2024). This tool automates Facebook's normal web UI, which Meta's Terms do not permit without their permission,
> and Facebook may restrict accounts that post the same content to many groups. The application is built to stay on
> the conservative side: it never handles passwords, never joins groups, paces every action, and **stops** the moment
> Facebook shows a login wall, checkpoint or restriction notice. Publishing is fully automatic by default; optional
> per-post approval can be switched on in Settings or per campaign.
> It contains no detection-evasion techniques. Use it only with your own account and within the rules of each group.

## Architecture

```
src/
  FacebookPublisher.Domain             Entities + enums (no dependencies)
  FacebookPublisher.Application        Use cases, interfaces, CampaignRunner, DTOs, validation
  FacebookPublisher.Infrastructure     EF Core (SQL Server), migrations, file storage, Excel/CSV export,
                                       campaign queue / cancellation tracker / approval gate
  FacebookPublisher.BrowserAutomation  Playwright session manager + Facebook page automation
  FacebookPublisher.Web                Blazor Server UI, SignalR hub, BackgroundService worker, Serilog
tests/
  FacebookPublisher.Tests              xUnit: URL/file validation, exports, campaign runner scenarios
```

Dependencies point inward: `Web → Infrastructure/BrowserAutomation → Application → Domain`.
The UI never touches Playwright; it calls Application services, which talk to `IFacebookAutomation`.

### Data model

| Table | Purpose |
|---|---|
| `FacebookGroups` | Configured groups (canonical URL is unique), last known membership |
| `ContentPosts` / `ContentAttachments` | Text + uploaded files, SHA-256 content hash |
| `Campaigns` | Content, pacing, review mode, status, row-version for safe status changes |
| `CampaignTargets` | One row per group per campaign (unique index), status, timestamp, error; group name/URL snapshotted so history survives deletes |
| `PublisherSettings` | Single row of user-editable settings |

### How a campaign runs

1. **Start** → targets are created, campaign is queued (`Channel<Guid>`); `CampaignWorker` runs campaigns one at a time.
2. **Session check** → no `c_user` cookie / login or checkpoint redirect ⇒ campaign **Halted**.
3. For each group (in order), with a random delay in `[min, max]` seconds between groups:
   1. *Checking Membership* → opens the group: Join button ⇒ **Skipped (Not a Member)**, unavailable ⇒ **Inaccessible**, composer visible ⇒ **Eligible**.
   2. *Duplicate check* → identical content published to this group inside the duplicate window ⇒ **Skipped (Duplicate)**.
   3. *Publishing* → saved to the database **before** Facebook is touched, then the composer is opened, text typed, files attached.
   4. *Awaiting Approval* — only if the optional approval mode is on (off by default) ⇒ post or **Skipped (Rejected)**.
   5. Post clicked ⇒ **Published Successfully**, or **Failed** with the reason. Failures never stop other groups.
4. Restriction/checkpoint/login wall at any point ⇒ campaign **Halted**; remaining groups stay *Pending*.
   Fix the problem, then **Resume** — already-processed groups are never posted to again.

**Duplicate-post protection:** unique (campaign, group) index · terminal statuses are never reprocessed · the attempt is
persisted before clicking Post · after a crash, in-flight targets become *Failed – outcome unknown* instead of retrying ·
content-hash window check · one active campaign per content item.

### Real-time updates

The **Execution Monitor** shows everything live: a "Now" panel (current group, latest step, countdown to the next group),
progress counters, the per-group table, and a **Live activity** feed with every step — session check, opening the
group, membership result, typing, attaching files, waiting for uploads, clicking Post, Facebook's confirmation,
skips, failures and the final summary. The last 1,000 entries per campaign are kept in memory, so reloading the page
mid-run shows the history (per-group results are always in the database).

`CampaignRunner` → `ICampaignProgressNotifier` → (a) an in-process broker that Blazor components subscribe to (their
UI updates are pushed over the Blazor Server SignalR circuit), and (b) the SignalR hub at `/hubs/campaigns`
(`CampaignChanged`, `TargetChanged`, `Message` events) for any external client.

## Prerequisites

- .NET 10 SDK
- SQL Server — LocalDB (installed with Visual Studio) works out of the box; any SQL Server 2016+ works
- Google Chrome (default browser). Edge or Playwright's bundled Chromium can be chosen in Settings → Browser.

## Run locally

```powershell
cd FacebookPublisher
dotnet tool restore                     # installs dotnet-ef from the tool manifest
dotnet run --project src/FacebookPublisher.Web
```

Open the URL shown (https://localhost:7077 or http://localhost:5160). The database is created/migrated automatically on
start-up. To use another SQL Server, change `ConnectionStrings:DefaultConnection` in
`src/FacebookPublisher.Web/appsettings.json` (or set the `ConnectionStrings__DefaultConnection` environment variable).

### First use

1. **Settings → Open login window** — a browser window opens on facebook.com. Sign in yourself (including 2FA).
2. **Settings → Verify session** — should report *Logged in*. The session lives in the browser-profile folder.
3. **Facebook Groups** — add group URLs (single or bulk, `Name | URL` per line). Optionally *Check access* (shield icon).
4. **Content Composer** — write text and attach files; check the live preview; save.
5. **Publishing Campaign** — pick content and groups, set the delay (default 10–20 s), start. Posting is fully automatic.
6. **Execution Monitor** — follow every step live in the activity feed; cancel or resume.
7. **Reports** — campaign history; export Excel or CSV.

Facebook UI language: selectors and phrases target the **English (US)** interface. Either use English or translate the
`FacebookAutomation:Selectors` / `Phrases` values in `appsettings.json` (all of them are configurable without recompiling).

## Configuration (`appsettings.json`)

| Section | Key settings |
|---|---|
| `ConnectionStrings:DefaultConnection` | SQL Server connection |
| `Storage:RootPath` | Upload folder (default `App_Data/uploads`) |
| `ContentUpload` | Max sizes per type, max attachments, max text length |
| `FacebookAutomation` | Probe/composer/post timeouts, `Selectors`, `Phrases` (restriction, inaccessible, checkpoint URL fragments) |
| `Serilog` | Console + rolling file logs in `logs/` |

User-editable settings (delays, review timeout, max groups per campaign, duplicate window, browser channel/profile,
headless, timeouts) are stored in the database and edited on the **Settings** page.

## Tests

```powershell
dotnet test
```

The runner tests use a fake `IFacebookAutomation`, so they verify skip/publish/halt/resume/duplicate/review/cancel
behaviour without contacting Facebook.

## When Facebook changes its UI

Symptoms: groups report *"Could not determine membership"* or *"composer is not available"*. Open the group in the
automation browser, inspect the Join button / "Write something…" box / Post button, and update the matching selector
arrays under `FacebookAutomation:Selectors` in `appsettings.json`. Restart the app.

## Deployment

This is a **single-user, local** tool: it drives a visible browser on the machine it runs on and has no login of its own.

- **Recommended:** run it on your workstation (`dotnet run`), or publish a self-contained build:
  ```powershell
  dotnet publish src/FacebookPublisher.Web -c Release -r win-x64 --self-contained -o publish
  .\publish\FacebookPublisher.Web.exe --urls http://localhost:5160
  ```
  Set `ConnectionStrings__DefaultConnection` if not using LocalDB.
- **Database migrations for a separate server:** `dotnet ef migrations script -p src/FacebookPublisher.Infrastructure -s src/FacebookPublisher.Web -o migrate.sql`
  (the app also migrates automatically at start-up).
- **Do not expose it to the internet** without adding authentication (e.g. ASP.NET Core Identity or Windows auth)
  in front of it — anyone with access could post as your Facebook account.
- Hosting on a server/VM requires a desktop session for the visible browser (login and review); headless mode only
  works after a session already exists in the profile folder.
