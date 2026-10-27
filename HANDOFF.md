# Orbit / Azure DevOps Client — Handoff

Last updated: 2026-10-24

## Purpose

Orbit is a native Windows desktop client for Azure DevOps. It is intended as a secondary company-standard option for programmers who prefer a focused Outlook-style work experience over the browser application.

The primary workflow is assigned work first: a list pane plus a reading pane, with description, acceptance criteria, comments, parent/children, and related items close together. Dashboards remain available, but are not the primary work driver. Accessibility and readability are first-class requirements.

Current checkpoint: the editing UX was redesigned after user review and is now accepted as “much much better.” UX takes precedence over exposing transport-layer details.

## Current stack

- WPF / .NET 10 Windows desktop application.
- Telerik UI for WPF 2026.3.812.
- Telerik NoXaml implicit-style dictionaries loaded globally, with runtime family choices for Fluent, Windows 11, Material, and Office 2019.
- Telerik `RadGridView` for table view.
- Telerik `RadComboBox` for filters, state, priority, creation type, and appearance selectors.
- Telerik `RadRichTextBox` plus HTML format providers for visual Azure DevOps rich-text editing.
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
  RichTextEditing.cs                 Telerik HTML visual-edit boundary
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
- Markdown comment creation and editing are implemented. HTML-backed descriptions, acceptance criteria, and legacy comments edit as rendered Telerik `RadRichTextBox` documents; HTML stays at the API boundary. Markdown/plain-text content keeps its original format.
- Read mode remains uncluttered. A pencil appears only on hover or keyboard focus; users can also double-click the content or press `F2`/`Enter` to edit inline. Single-click remains available for selection, copying, and link interaction.
- The visual HTML editor exposes compact bold/italic/underline controls, Telerik keyboard shortcuts, context menus, and the selection mini-toolbar. It never displays raw HTML.
- New and existing Markdown comments use a compact themed Markdown editor. The custom `MarkdownEditor` template in `WorkspaceResources.xaml` is required for readable contrast across the shell themes.
- Rich-text import blocks network image downloads while preserving image URIs for HTML round trips. Links, tables, headings, formatting, and image URIs are exercised by the desktop smoke test.
- Unsaved visual-rich-text edits are detected through Telerik document history, so navigation protection does not depend solely on delayed content-change events.

### Accessibility and desktop behavior

- Telerik theme families: Fluent, Windows 11, Material, and Office 2019.
- Dark, Light, and High contrast color variations.
- Font family choices: Segoe UI, Arial, Calibri, Tahoma, Verdana.
- Reading sizes: 80%, 90%, 100%, 115%, 135%, 155%, 180%.
- Settings persist in `appsettings.local.json`.
- Telerik palettes switch for Dark/Light/High contrast; the custom appearance layer handles global font scale and shell resources. Telerik's implicit-style `System.Windows.xaml` dictionary themes support native WPF controls as well as Telerik controls.
- Theme architecture deliberately uses NoXaml assemblies plus merged theme dictionaries. This is the Telerik-supported foundation for changing the application theme at runtime; do not reintroduce the Xaml/StyleManager package mix.
- Application opens maximized.
- Last monitor is remembered in `window-placement.json` beside the executable, with safe fallback if the monitor is disconnected.
- Default layout density is intentionally compact: shell chrome, cards, filters, tabs, queue rows, and table rows use restrained padding so screen real estate is reserved for work content. Accessibility font family and scale controls remain independent and must not be sacrificed when tightening layout spacing.

## Configuration

Create `appsettings.local.json` beside the executable, or use Settings in the app:

```json
{
  "ProjectUrl": "https://dev.azure.com/organization/project",
  "PersonalAccessToken": "your-pat",
  "Team": "",
  "UseDemoData": false,
  "MaxWorkItems": 500,
  "ThemeFamily": "Fluent",
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

Current verified result: **49 xUnit test cases passed**.

The xUnit suite covers settings defaults, URL validation, theme-family/font/scale appearance validation and persistence, REST contracts, real loopback HTTP integration, batching, comments paging, description-field updates, Markdown comment creation/editing, cancellation, sanitized failures, relationships, demo mutations, and work-item edits. The integration server is local and never needs a PAT.

The desktop smoke test is separate because it requires a real WPF process:

```powershell
$env:ORBIT_SMOKE_DIR = "$PWD\artifacts\screenshots"
& .\artifacts\Orbit\DevOpsClient.exe --smoke
Get-Content .\artifacts\screenshots\smoke.txt
```

The smoke test always uses demo data and verifies the reading workflow, quiet hover/F2 edit affordances, Telerik visual HTML editing, supported HTML round trips (links, tables, formatting, and image URIs), Markdown rendering/authoring, visual legacy-comment editing, unsaved-draft tracking, stale-selection protection, comments, relations, Telerik selectors, runtime Fluent/Windows 11/Material/Office 2019 family switching, Dark/Light/High contrast palette switching, search/filtering, create/edit/refresh, board moves, and compact layouts.

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

The artifact has been rebuilt and smoke-tested after the current editing, test, and Telerik-control work.

Useful visual checkpoints:

```text
artifacts/screenshots/work-description-editor.png
artifacts/screenshots/work-html-comment-editor.png
artifacts/screenshots/work-comment-editor.png
artifacts/screenshots/smoke.txt
```

## Known limitations

- No live Azure DevOps integration test with a real account/PAT.
- No CI workflow or coverage threshold yet.
- Desktop smoke is process-level and screenshot-oriented, not a full UI automation suite.
- Comment deletion, attachments, offline cache, retry queue, and encrypted credential storage are not implemented. Telerik's visual editor covers common Azure DevOps HTML rather than arbitrary browser HTML; untouched fields retain their exact original API value.
- Board view is a state-category summary, not a full Azure team-board replacement with swimlanes, WIP limits, ordering, or sprint configuration.
- Dashboard widgets remain hosted by Azure DevOps and open in the browser.

## Recommended next increments

1. Add CI that runs `dotnet test`, build, and the smoke test on a Windows runner.
2. Add coverage collection and minimum thresholds for `DevOpsClient.Core`.
3. Add structured tests for Markdown edge cases and appearance palette behavior.
4. Add a separate opt-in live integration test profile using environment-provided credentials, never committed secrets.
5. Extend the compact rich-text toolbar only in response to real usage feedback; preserve the low-chrome reading experience.
6. Add targeted visual-editor tests for paste, undo/redo, hyperlinks, lists, tables, and Azure-hosted images.
7. Add encrypted credential storage and a company deployment/upgrade strategy.
8. Add richer board configuration only after the core assigned-work workflow remains stable.

## Important design decisions

- Keep the assigned-work reading experience primary; dashboards are secondary.
- UX takes precedence: transport formats such as HTML belong at the API boundary, not in the normal editing experience.
- Keep edit affordances quiet but discoverable: hover/focus pencil plus double-click and `F2`/`Enter`; do not consume ordinary single-click selection.
- Prefer native selectable WPF content for descriptions/comments instead of embedding a browser.
- Preserve the original API value until the user makes and saves a real edit. Telerik may normalize supported HTML on an edited save, so expand format support deliberately and keep round-trip probes in the smoke test.
- Treat server-provided relation URLs as untrusted; resolve only same-organization work item IDs through the configured endpoint.
- Use revision guards for writes to avoid overwriting concurrent changes.
- Keep demo mode deterministic and safe; smoke tests must never use live settings.
- Keep tests credential-free by default.
