# Orbit / Azure DevOps Client — Handoff

Last updated: 2026-10-24

## Purpose

Orbit is a native Windows desktop client for Azure DevOps. It is intended as a secondary company-standard option for programmers who prefer a focused Outlook-style work experience over the browser application.

The primary workflow is assigned work first: a list pane plus a reading pane, with description, acceptance criteria, comments, parent/children, and related items close together. Dashboards remain available, but are not the primary work driver. Accessibility and readability are first-class requirements.

## Current stack

- WPF / .NET 10 Windows desktop application.
- Telerik UI for WPF 2026.3.812.
- Telerik Fluent theme loaded globally.
- Telerik `RadGridView` for table view.
- Telerik `RadComboBox` for filters, state, priority, creation type, and appearance selectors.
- Custom WPF shell for the Outlook-style layout.
- Azure DevOps REST API 7.1.
- PAT authentication over HTTPS.

## Repository layout

```text
src/DevOpsClient.Core
  Models.cs                         Settings and domain contracts
  AzureDevOpsService.cs             Azure DevOps REST client
  AzureDevOpsService.Reading.cs     Assigned work, relations, comments
  DemoProjectService*.cs            In-memory demo workspace

src/DevOpsClient
  MainWindow.xaml                    WPF shell
  MainWindow.xaml.cs                 Navigation, views, settings, editor
  MainWindow.Workspace.cs            Assigned-work reading pane
  MainWindow.WorkspaceSmoke.cs       Desktop smoke workflow
  ReadableDocument.cs                Safe HTML/Markdown native renderer
  Appearance.cs                      Theme palette and font scaling
  WindowPlacementStore.cs            Maximized monitor persistence

tests/DevOpsClient.Core.Tests
  SettingsTests.cs                   Structured settings/accessibility tests
  AzureDevOpsServiceTests.cs         REST contract/unit tests
  AzureDevOpsServiceIntegrationTests.cs
                                     Real loopback HTTP pipeline tests
  DemoProjectServiceTests.cs         Demo behavior tests

artifacts/Orbit                       Published executable (generated)
artifacts/screenshots                 Smoke screenshots/results (generated)
```

## Implemented functionality

### Work experience

- Assigned to me uses server-side WIQL with `System.AssignedTo = @Me`.
- All work and Recently viewed views.
- Search, type/owner filters, completed-item toggle.
- Reading pane with Description, Comments, and Related tabs.
- Parent, child, related, predecessor/successor, and duplicate links.
- Open by number (`Ctrl+G`) and copy work item number.
- Status changes from the reading pane.
- Telerik table view with sorting and filtering.
- Board view with state-category columns and state moves.
- Creation and editor updates with revision-checked JSON Patch.
- Dashboard discovery and browser links.

### Content

- Native safe HTML rendering; no embedded browser for work-item reading.
- Native Markdown rendering for headings, lists, quotes, emphasis, inline/fenced code, HTTPS links, and image placeholders.
- Azure rendered comment HTML is preferred; raw Markdown comments are rendered directly.
- No Markdown authoring/comment posting yet; current Markdown support is read/display focused.

### Accessibility and desktop behavior

- Dark, Light, and High contrast themes.
- Font family choices: Segoe UI, Arial, Calibri, Tahoma, Verdana.
- Reading sizes: 80%, 90%, 100%, 115%, 135%, 155%, 180%.
- Settings persist in `appsettings.local.json`.
- Telerik Fluent palette switches for Dark/Light; the custom appearance layer handles global font scale and High contrast behavior.
- Application opens maximized.
- Last monitor is remembered in `window-placement.json` beside the executable, with safe fallback if the monitor is disconnected.

## Configuration

Create `appsettings.local.json` beside the executable, or use Settings in the app:

```json
{
  "ProjectUrl": "https://dev.azure.com/organization/project",
  "PersonalAccessToken": "your-pat",
  "Team": "",
  "UseDemoData": false,
  "MaxWorkItems": 500,
  "Theme": "Dark",
  "FontFamily": "Segoe UI",
  "FontScale": 1.0
}
```

PAT guidance:

- Work Items: Read for browsing.
- Work Items: Read & write for create/update.
- Dashboard: Read for dashboard discovery.
- Never commit `appsettings.local.json` or paste credentials into source files.

Live Azure DevOps has not been exercised in this workspace because no project URL/PAT was supplied. The client is designed to be pointed at a live project through local settings.

## Test strategy

The structured suite is the canonical automated test entry point:

```powershell
dotnet restore DevOpsClient.sln --configfile NuGet.Config
dotnet build DevOpsClient.sln --no-restore
dotnet test DevOpsClient.sln --no-build --no-restore
```

Current verified result: **40 xUnit test cases passed**.

The xUnit suite covers settings defaults, URL validation, appearance validation and persistence, REST contracts, real loopback HTTP integration, batching, comments paging, cancellation, sanitized failures, relationships, demo mutations, and work-item edits. The integration server is local and never needs a PAT.

The desktop smoke test is separate because it requires a real WPF process:

```powershell
$env:ORBIT_SMOKE_DIR = "$PWD\artifacts\screenshots"
& .\artifacts\Orbit\DevOpsClient.exe --smoke
Get-Content .\artifacts\screenshots\smoke.txt
```

The smoke test always uses demo data and verifies the reading workflow, Markdown/HTML rendering, stale-selection protection, comments, relations, Telerik selectors, search/filtering, create/edit/refresh, board moves, and compact layouts.

The release pipeline runs restore, Release build, `dotnet test`, and publish:

```powershell
.\Publish-Orbit.ps1
```

The lazy launcher builds, tests, publishes, and runs the packaged app:

```powershell
.\Start-Orbit.ps1
```

## Current release artifact

```text
D:\Development\Personal\DevOpsClient\artifacts\Orbit\DevOpsClient.exe
```

The artifact has been rebuilt and smoke-tested after the current test and Telerik-control work.

## Known limitations

- No live Azure DevOps integration test with a real account/PAT.
- No CI workflow or coverage threshold yet.
- Desktop smoke is process-level and screenshot-oriented, not a full UI automation suite.
- Description editing, Markdown authoring, comment posting, attachments, offline cache, retry queue, and encrypted credential storage are not implemented.
- Board view is a state-category summary, not a full Azure team-board replacement with swimlanes, WIP limits, ordering, or sprint configuration.
- Dashboard widgets remain hosted by Azure DevOps and open in the browser.

## Recommended next increments

1. Add CI that runs `dotnet test`, build, and the smoke test on a Windows runner.
2. Add coverage collection and minimum thresholds for `DevOpsClient.Core`.
3. Add structured tests for Markdown edge cases and appearance palette behavior.
4. Add a separate opt-in live integration test profile using environment-provided credentials, never committed secrets.
5. Add Markdown authoring/posting with explicit Azure DevOps HTML/Markdown conversion rules.
6. Add encrypted credential storage and a company deployment/upgrade strategy.
7. Add richer board configuration only after the core assigned-work workflow remains stable.

## Important design decisions

- Keep the assigned-work reading experience primary; dashboards are secondary.
- Prefer native selectable WPF content for descriptions/comments instead of embedding a browser.
- Treat server-provided relation URLs as untrusted; resolve only same-organization work item IDs through the configured endpoint.
- Use revision guards for writes to avoid overwriting concurrent changes.
- Keep demo mode deterministic and safe; smoke tests must never use live settings.
- Keep tests credential-free by default.
