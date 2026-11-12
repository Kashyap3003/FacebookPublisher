# Facebook Group Content Distribution (Group Publisher)

A Blazor Server application that distributes text, images, videos and PDFs to Facebook groups the logged-in account
already belongs to, using Microsoft Playwright to drive a browser profile the user signs into manually. Groups are
processed in randomly sized batches with configurable delays, campaigns can be stopped and resumed at any time, and all
progress is stored in SQL Server so nothing is ever posted twice to the same group.

> **Read this first.** Meta provides **no official API** for posting to groups (Groups API publishing was removed in
> April 2024). This tool automates Facebook's normal web UI, which Meta's Terms do not permit without their permission,
> and Facebook may restrict accounts that post the same content to many groups. The application is built to stay on
> the conservative side: it never handles passwords, never joins groups, paces every action, and **stops** the moment
> Facebook shows a login wall, checkpoint or restriction notice. Publishing is fully automatic by default; optional
> per-post approval can be switched on in Settings or per campaign.
> It contains no detection-evasion techniques. Use it only with your own account and within the rules of each group.

## Contents

- [Features](#features)
- [Architecture](#architecture)
- [Batch processing and delays](#batch-processing-and-delays)
- [Stop, Resume and recovery](#stop-resume-and-recovery)
- [Statuses](#statuses)
- [Real-time monitoring](#real-time-monitoring)
- [Prerequisites](#prerequisites)
- [Run locally](#run-locally)
- [Configuration](#configuration)
- [Database and migrations](#database-and-migrations)
- [Tests](#tests)
- [When Facebook changes its UI](#when-facebook-changes-its-ui)
- [Deployment](#deployment)

## Features

| Area | What it does |
|---|---|
| **Groups** | Add, edit, enable/disable and remove group links, one at a time or in bulk (`URL` or `Name \| URL` per line). Links are validated and normalized; duplicates are rejected. **Share links** (`facebook.com/share/g/…`) are opened in the logged-in browser to find the real group address and name. Optional per-group "check access" (never joins). |
| **Content** | Text plus images, videos or a single PDF, validated by type and size, with a live post preview. |
| **Campaigns** | Pick content and groups, configure batches and delays, then **Start campaign** or **Save without starting**. |
| **Execution** | Membership check → duplicate check → publish, group by group, in random batches with random delays. A failing group never stops the others; a Facebook restriction stops the whole campaign. |
| **Stop / Resume** | Stop at any moment (including during a multi-hour break); Resume later — even after a restart — with only the remaining groups. |
| **Monitoring** | Live Execution Monitor: batch state, delay countdown, counters, per-group table and a step-by-step activity feed. |
| **Reports** | Campaign history with per-group results and reasons; Excel and CSV export. |

## Architecture

```
src/
  FacebookPublisher.Domain             Entities + enums (no dependencies)
  FacebookPublisher.Application        Use cases, interfaces, CampaignRunner, batch/delay services, DTOs, validation
  FacebookPublisher.Infrastructure     EF Core (SQL Server), migrations, file storage, Excel/CSV export,
                                       campaign queue / execution tracker / approval gate / random source / delay
  FacebookPublisher.BrowserAutomation  Playwright session manager + Facebook page automation (or a disabled stub)
  FacebookPublisher.Web                Blazor Server UI, SignalR hub, campaign worker, demo login, Serilog
tests/
  FacebookPublisher.Tests              xUnit: unit tests + SQL Server LocalDB integration tests
```

Dependencies point inward: `Web → Infrastructure/BrowserAutomation → Application → Domain`.
The UI never touches Playwright; it calls Application services, which talk to `IFacebookAutomation`.

**Campaign execution** has exactly one engine, `CampaignRunner`. It runs only when the user clicks **Start** or
**Resume**: those put the campaign id on an in-memory queue, and the single `CampaignWorker` processes queued campaigns
one at a time (there is one browser profile). There are no schedulers, recovery workers or automatic restarts.

### Data model

| Table | Purpose |
|---|---|
| `FacebookGroups` | Configured groups (canonical URL is unique), last known membership |
| `ContentPosts` / `ContentAttachments` | Text + uploaded files, SHA-256 content hash |
| `Campaigns` | Content, status, **batch/delay configuration snapshot** (`Pacing_*` columns), **batch execution state** (current batch, size, progress, completed batches, extended-break counter, remaining groups, pending delay), execution lease (owner + heartbeat) and a row version |
| `CampaignTargets` | One row per group per campaign (unique index): status, batch number, publish attempts, timestamp, error. Group name/URL are snapshotted so history survives deletes |
| `PublisherSettings` | Single row of user-editable settings, including the **default** batch/delay configuration |

### How a campaign runs

1. **Start / Resume** → the campaign is queued; `CampaignWorker` hands it to `CampaignRunner`, which claims it in the
   database (status `Running`, owner = this process) using the row version, so it can never run twice.
2. **Session check** → no `c_user` cookie, login or checkpoint redirect ⇒ campaign **Halted**.
3. Only groups that are **Pending** in the database are processed, in order, inside batches (see below). For each group:
   1. *Checking Membership* → opens the group: Join button ⇒ **Skipped (Not a Member)**, unavailable ⇒ **Inaccessible**, composer visible ⇒ **Eligible**.
   2. *Duplicate check* → identical content published to this group inside the duplicate window ⇒ **Skipped (Duplicate)**.
   3. *Publishing* → saved to the database **before** Facebook is touched, then the composer is opened, text typed, files attached.
   4. *Awaiting Approval* — only if the optional approval mode is on (off by default) ⇒ post or **Skipped (Rejected)**.
   5. Post clicked ⇒ **Published Successfully**, or **Failed** with the reason. The result is saved immediately.
4. Restriction, checkpoint or login wall at any point ⇒ campaign **Halted**; remaining groups stay Pending.
   Fix the problem, then **Resume**.

**Duplicate-post protection:** unique (campaign, group) index · only Pending groups are ever processed · the publish
attempt is saved before clicking Post · groups caught mid-publish by a crash become **Outcome Unknown** and are never
retried automatically · content-hash window check · one active campaign per content item · row-version lease against
concurrent execution.

> Facebook has no transactional posting API, so exactly-once delivery cannot be guaranteed if the application dies
> after Facebook accepted a post but before the result was saved. That case is surfaced as **Outcome Unknown** for you
> to verify instead of being retried.

## Batch processing and delays

Configured under **Settings → Advanced Batch Processing & Delay Settings** (defaults for new campaigns) and on the
**Publishing Campaign** page (per campaign). Each campaign stores its own copy, so changing Settings never affects a
campaign that already exists, including when it is resumed.

| Setting | Default | Units | Allowed |
|---|---|---|---|
| Batch size | 5 – 9 groups | — | 1 – 100 |
| Delay between posts | 10 – 30 | seconds / minutes | 5 s – 1 h |
| Delay between batches | 15 – 30 | minutes / hours | 0 – 24 h |
| Apply extended delay after | 5 completed batches | — | 1 – 1000 |
| Extended delay | 3 – 5 | minutes / hours | 0 – 72 h |

Rules (all minimum/maximum values are **inclusive**; a new random value is picked for every batch and every delay,
in whole seconds):

1. A random batch size is chosen; groups of the batch are processed one after another.
2. Between two groups **in the same batch**, a random *post delay*. None after the last group of a batch.
3. After a batch completes:
   - every *N* completed batches, a random **extended delay** — it **replaces** the normal batch delay (never both);
   - otherwise a random **delay between batches**;
   - no delay at all if no groups remain.
4. A final batch with fewer groups is processed normally and counted.
5. Every processed group (published, skipped, inaccessible or failed) counts toward the batch.
6. Only fully completed batches count toward the extended-delay counter; an interrupted batch is counted once, when it
   actually finishes after a resume.

Random values come from `IBatchDelayService` (`GenerateRandomBatchSize`, `GenerateRandomPostDelay`,
`GenerateRandomBatchDelay`, `GenerateRandomExtendedDelay`), which uses an injectable `IRandomSource` so tests can make
them deterministic. Waiting uses `Task.Delay` with the campaign's `CancellationToken` (never `Thread.Sleep`), in
one-minute chunks that also refresh the campaign's heartbeat, so **Stop interrupts even a five-hour break immediately**.

Validation runs on every keystroke in the UI and again on the server; invalid values cannot be saved or started.

## Stop, Resume and recovery

| Action | Behaviour |
|---|---|
| **Start campaign** | Starts a campaign saved with *Save without starting*, or (on the Publishing Campaign page) creates and starts a new one. All groups begin as Pending. |
| **Stop campaign** | Cancels the running campaign, including any delay in progress. Published groups stay published, the remaining groups stay **Pending**, batch counters are saved, status becomes **Stopped**. A group interrupted before its post was submitted goes back to Pending. |
| **Resume campaign** | Available for Stopped, Interrupted, Halted and finished campaigns that still have Pending groups. Processes **only** the Pending groups, with the campaign's original batch/delay configuration and batch counters. |

**Resume policy for delays:** the end time of the current delay is stored in the database. If you stop during a delay,
Resume first waits whatever is left of it (nothing if that time has already passed). This keeps the pacing honest
without ever waiting automatically after a restart.

**Example — 100 groups:** 60 are published, then you stop (or the PC shuts down). After reopening the app and
clicking **Resume**, the database identifies the 60 Published groups, skips them without even opening them, and
processes only groups 61–100. This exact scenario is covered by an automated test against SQL Server.

**Application shutdown or crash:**

- Nothing is resumed or re-queued when the application starts.
- When you next open the Dashboard, Execution Monitor or Reports, campaigns left Queued/Running by the previous run are
  marked **Interrupted**. Groups that were mid-publish become **Outcome Unknown**; groups that were mid-check return to
  Pending. All Published statuses and batch counters are preserved.
- **Outcome Unknown** groups are never retried automatically. The Execution Monitor lists them with two buttons:
  *Yes, it was published* (marks them Published, excluded from every resume) and *No — return to Pending* (processed by
  the next Resume). Stop the campaign before verifying.

## Statuses

**Campaign:** Not Started · Queued · Running · Stopped · Interrupted · Halted (action required, e.g. login or
restriction) · Completed · Completed with Errors. *(Cancelled exists only on campaigns created before Stop/Resume; they
can be resumed too.)*

**Group:** Pending · Checking Membership · Eligible · Publishing · Awaiting Approval · Published Successfully ·
Skipped (Not a Member / Duplicate / Rejected) · Failed · Inaccessible · Outcome Unknown.

## Real-time monitoring

The **Execution Monitor** shows:

- **Controls:** Start, Stop and Resume, depending on the campaign status.
- **Counters:** total groups, published, remaining, failed, unknown outcome, skipped, inaccessible, % complete.
- **Batch processing:** current batch (and estimated total), current batch size, groups completed in the batch,
  completed batches, remaining groups, extended-break progress, current delay type (between posts / between batches /
  extended break), selected delay, remaining delay with a live countdown, and the next scheduled action.
- **Live activity feed:** every step — session check, batch start, opening the group, membership result, typing,
  attaching files, uploads, clicking Post, Facebook's confirmation, each delay, skips, failures and the final summary.
  The last 1,000 entries per campaign are kept in memory, so reloading mid-run shows the history.
- **Outcome Unknown** verification list and the per-group table (with batch numbers).

All batch and delay information comes from the database, so the monitor is correct after a page reload or restart.

`CampaignRunner` → `ICampaignProgressNotifier` → (a) an in-process broker that Blazor components subscribe to (their
UI updates are pushed over the Blazor Server SignalR circuit), and (b) the SignalR hub at `/hubs/campaigns`
(`CampaignChanged`, `TargetChanged`, `Message` events) for any external client.

## Users, sign-in and Facebook sessions

Group Publisher is multi-user: everyone signs in with their own account and sees only their own groups, content,
campaigns, reports and uploads, and posts through their own Facebook session.

**First administrator.** No accounts are built in. Before the first start, set environment variables (or user-secrets):

```powershell
$env:InitialAdmin__Email = "you@company.com"
$env:InitialAdmin__Password = "a-strong-password"   # at least 10 characters
```

On startup the app creates that administrator (on an upgraded install it also takes ownership of all existing data).
Afterwards the variables can be removed. There is no public registration: administrators create, enable, disable,
promote and reset users under **User Management**. Five wrong passwords lock an account for 15 minutes; disabling a
user signs them out within a minute and stops their running campaign.

**Facebook login.** Each user opens **Settings → Open Facebook login**, signs in to Facebook in the Chrome window that
appears (Group Publisher never asks for or stores the Facebook password), then clicks **Verify**. The session lives in a
private Chrome profile at `App_Data/BrowserProfiles/{UserId}`; **Disconnect** deletes it. Profiles are never shared or
served over HTTP. Uploads are stored per user under `uploads/{UserId}`.

**Concurrency.** Different users' campaigns run at the same time (up to `Campaigns:MaxConcurrentCampaigns`, default 10).
Each user runs one campaign at a time; their further campaigns queue.

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

1. **Settings → Open login window** — a Chrome window opens on facebook.com. Sign in yourself (including 2FA).
2. **Settings → Verify session** — should report *Logged in*. The session lives in the browser-profile folder.
3. **Settings → Advanced Batch Processing & Delay Settings** — review the default batch sizes and delays; *Save settings*.
4. **Facebook Groups** — add group links or share links (single or bulk). Optionally *Check access* (shield icon).
5. **Content Composer** — write text and attach files; check the live preview; save.
6. **Publishing Campaign** — pick content and groups, adjust the batch/delay settings for this campaign if needed
   (estimated batches and duration are shown), then **Start campaign** or **Save without starting**.
7. **Execution Monitor** — follow batches, delays and every step live; **Stop** and **Resume** at any time; verify any
   *Outcome Unknown* groups.
8. **Reports** — campaign history including the batch/delay configuration used; export Excel or CSV.

Facebook UI language: selectors and phrases target the **English (US)** interface. Either use English or translate the
`FacebookAutomation:Selectors` / `Phrases` values in `appsettings.json` (all of them are configurable without recompiling).

## Configuration

### `appsettings.json`

| Section | Key settings |
|---|---|
| `ConnectionStrings:DefaultConnection` | SQL Server connection |
| `Hosting` | `AutomationEnabled` (default `true`; `false` = online demo without a browser), `RequireLogin` (default `false`), `LoginUsername`, `LoginPassword` |
| `Storage:RootPath` | Upload folder (default `App_Data/uploads`) |
| `ContentUpload` | Max sizes per type, max attachments, max text length |
| `FacebookAutomation` | Probe/composer/post timeouts, `Selectors`, `Phrases` (restriction, inaccessible, checkpoint URL fragments) |
| `Serilog` | Console + rolling file logs in `logs/` |

Every setting can be overridden with an environment variable using `__` as the separator, e.g.
`Hosting__RequireLogin=true`.

### Settings page (stored in the database)

Batch/delay defaults, optional approval mode and its timeout, max groups per campaign, duplicate-protection window,
browser (Chrome / Edge / bundled Chromium), browser profile folder, headless mode, page-load and upload timeouts.

## Database and migrations

Migrations are applied automatically at start-up. They live in
`src/FacebookPublisher.Infrastructure/Persistence/Migrations`:

| Migration | Purpose |
|---|---|
| `InitialCreate` | Groups, content, campaigns, targets, settings |
| `AutomaticPublishingWithChrome` | Defaults: automatic publishing, Chrome |
| `BatchProcessingAndResume` | Batch/delay configuration (settings + per-campaign snapshot), batch execution state, execution lease, batch number per group; carries the previous single delay over into "delay between posts" and renames `Draft` to `NotStarted` |

Useful commands:

```powershell
# Apply migrations to a specific database
dotnet ef database update -p src/FacebookPublisher.Infrastructure -s src/FacebookPublisher.Infrastructure --connection "<connection string>"

# Generate an SQL script (e.g. for a DBA or a separate server)
dotnet ef migrations script -p src/FacebookPublisher.Infrastructure -s src/FacebookPublisher.Infrastructure -o migrate.sql
```

## Tests

```powershell
dotnet test
```

- **Unit tests:** URL normalization, share links, file validation, report export, batch-size and delay randomization
  (inclusive bounds, whole seconds), batch/delay validation.
- **Runner tests (in-memory):** publish, skip, inaccessible, failure isolation, halt, review, stop, duplicate window.
- **SQL Server integration tests** (real LocalDB database per test, real migrations, dropped afterwards): delay order
  (post / batch / extended precedence, nothing after the last group), partial final batch, Stop interrupting a long
  real `Task.Delay`, Stop during post and extended delays, the **100-group stop-after-60 / restart / resume** scenario,
  crash recovery with Outcome Unknown and manual verification, no automatic resume at start-up, two simultaneous Resume
  requests, two racing runners, completed campaigns not republishing, settings persistence and snapshot isolation.

All tests use a fake `IFacebookAutomation`, so they never contact Facebook. The integration tests need SQL Server
LocalDB (`(localdb)\MSSQLLocalDB`).

## When Facebook changes its UI

Symptoms: groups report *"Could not determine membership"* or *"composer is not available"*. Open the group in the
automation browser, inspect the Join button / "Write something…" box / Post button, and update the matching selector
arrays under `FacebookAutomation:Selectors` in `appsettings.json`. Restart the app.

## Deployment

This is a **single-user** tool: posting drives a visible browser with your Facebook login on the machine it runs on.
Run one instance per database.

### On your PC (full functionality)

```powershell
dotnet publish src/FacebookPublisher.Web -c Release -r win-x64 --self-contained -o publish
.\publish\FacebookPublisher.Web.exe --urls http://localhost:5160
```

Set `ConnectionStrings__DefaultConnection` if not using LocalDB. To let others watch a live demo, keep it running on
your PC and share it through a tunnel (Microsoft Dev Tunnels or ngrok) restricted to your organization, with
`Hosting__RequireLogin=true` and a strong `Hosting__LoginPassword`.

### Online demo (Azure App Service, no Facebook posting)

Cloud hosts have no desktop or Facebook login, so the hosted copy runs with browser automation **off**: every screen
works, a banner explains that publishing is disabled, and campaigns stop at the session check. Settings used for the
Azure for Students demo (free F1 Linux plan + Azure SQL Database free offer):

| App setting | Value |
|---|---|
| `Hosting__AutomationEnabled` | `false` |
| `Hosting__RequireLogin` | `true` |
| `Hosting__LoginUsername` / `Hosting__LoginPassword` | demo credentials |
| `Storage__RootPath` | `/home/data/uploads` |
| `ASPNETCORE_FORWARDEDHEADERS_ENABLED` | `true` |
| Connection string `DefaultConnection` (type SQLAzure) | Azure SQL connection string |
| Startup command | `dotnet FacebookPublisher.Web.dll` |
| WebSockets | enabled (required by Blazor Server) |

Deploy (Playwright's browser drivers are not needed there):

```powershell
dotnet publish src/FacebookPublisher.Web -c Release -o publish -p:PlaywrightPlatform=none
# zip the contents of ./publish (forward-slash paths, e.g. with tar.exe -a -c -f app.zip *), then:
az webapp deploy -g <resource-group> -n <web-app> --src-path app.zip --type zip
```

The database is migrated automatically on first start. The free plan sleeps when idle, so the first visit can take
20–60 seconds.

### Security

- **Never expose the app to the internet without `Hosting:RequireLogin=true`** (or another authentication method) — anyone
  with access could post as your Facebook account.
- Passwords and connection strings belong in environment variables / App Service settings, never in source control.
