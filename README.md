# Vibedev

A Windows desktop dashboard for coding projects, built with .NET 10 and WPF. Dark editor-style UI, recursively split project panels and floating windows.

## Start

```powershell
dotnet run --project src/Vibedev
```

Requires Windows 10 version 2004 or later, the .NET 10 SDK, Git on PATH, and Microsoft Edge WebView2 Runtime. Terminal assets are bundled; Node is only needed to regenerate them.

To create a portable, self-contained Windows x64 build:

```powershell
dotnet publish src/Vibedev -c Release -r win-x64 --self-contained true -o artifacts/Vibedev
```

Run `artifacts/Vibedev/Vibedev.exe`. Keep the whole published folder together. Git and WebView2 Runtime must still be installed.

## Workspace

- **Add project** on the dashboard or the activity rail adds an existing local folder. Non-Git folders can also be edited and run.
- **Scan a directory** remembers one search root and finds Git repositories throughout its subdirectories, including nested repositories and Git worktrees. Git metadata, dependency/build output directories and directory links are skipped. Unreadable directories are listed in task output.
- Click a project in the sidebar or dashboard to open its panel. Filter using the sidebar field.
- Click and hold a project name, then drag to move its panel. Drop on a docking target to arrange it beside another panel, or elsewhere to leave it as a floating window. Drag a project out of the sidebar to create a new window, including another window for an already open project. A click without dragging still opens the existing panel.
- The compact dashboard shows open projects in small frames without numbers, in panel order, with their branch and whether they have changes, including background panels, splits and floating windows. Closing a project's last panel removes its frame; it stays registered in the sidebar. **Pull all** lives on the dashboard and includes all registered projects.
- The dashboard has its own row above the project workspace. Opening, splitting, docking and floating projects never replace it or use its space. The activity-rail button shows or hides this row and lights up while it is visible. Dashboard visibility is remembered; older layouts automatically move the dashboard out of project panes.
- Hide or show the Projects sidebar with the activity-rail button or `Ctrl+B`; its close button also hides it. Visibility and width are remembered.
- **Split → / Split ↓** opens a project beside the current panel; repeat inside either pane to nest layouts. A project can appear in multiple panels.
- Each project panel has a distinct frame and a top header with its name, folder path and close button, above the run controls. Project tab strips stay hidden. Switch projects through the sidebar or dashboard. **Float** detaches a panel. Dock floating panels before splitting them.
- Right-click a project name to move left/right, **Replace project** with another registered project, or **Swap with project**. Replacement checks unsaved edits and keeps the panel's position, including floating windows. Swaps preserve each panel's editors and work across panel groups, splits and floating windows.
- Hide or show the terminal and task output with the activity-rail terminal button, the panel's close button or `Ctrl+backtick`. Its height and visibility are remembered; running a project reveals it again.
- Right-click a sidebar project to remove it from the workspace. Its files stay on disk.
- Projects, scan root, panel sizes, floating positions and open file paths persist in `%LOCALAPPDATA%/Vibedev`. Unsaved edits are handled before closing; unsaved content itself is not persisted.

## Editing and Git

Double-click an explorer file to open it. Edits autosave after 800 ms without typing, including in background tabs. The editor footer shows autosave status; disk conflicts or write failures pause autosave and preserve your edits for explicit resolution with `Ctrl+S`. `Ctrl+S` also saves immediately, `Ctrl+F` finds, `Ctrl+H` opens replace and `Ctrl+W` closes the file. Search and replace use literal, case-insensitive text. Editors provide line numbers, syntax highlighting, undo/redo and C# indentation.

C# files use [csharp-ls](https://github.com/razzmatazz/csharp-language-server) for member completion (`Ctrl+Space` or typing `.`), error/warning underlines, hover information and go-to-definition (`F12`). Changes are synchronized before saving. Click **C# server** in a project panel to install version 0.26.0 into `%LOCALAPPDATA%/Vibedev/LanguageServer`, or to retry after a server failure. Installation requires the .NET 10 SDK and NuGet access. An existing global/PATH installation is also detected. Server startup and failures appear in task output; each panel owns and disposes its own session. VS Code extensions and debugger breakpoints are not implemented.

Each project panel has **Explorer**, **Search** and **Git** tabs. `Ctrl+Shift+F` opens project search; `Ctrl+P` opens filename search. Choose filenames, file contents or both. Results include unsaved text in that panel's editors, and double-clicking a result opens its matching line. Search respects Git ignore rules, skips binary files, build/dependency folders and directory links, and limits results to 500 matches. The file tree uses folder/file icons, nested indentation and full-row selection; refreshing preserves expanded folders and the selected file.

UTF-8, UTF-16 and UTF-32 with supported BOMs are preserved, along with line endings. Binary files and files over 8 MB are refused. Saving detects external changes and asks before overwriting them. A debounced file watcher updates the tree after creates, renames and deletions and reloads clean editors after external changes. Dirty buffers are preserved. Deleted open files remain available in their tabs; saving them requires explicit overwrite confirmation.

The **Git** tab shows branch/status and separate **Staged** and **Changes** lists. Click a file to open a colored unified diff. Ctrl/Shift+click selects multiple files; **+ Selected** saves and stages only those files, and **− Selected** unstages them while preserving working copies. Enter a message and **Commit staged** to commit the index. **Pull** and **Push** are explicit actions in the Git tab. Git-ignored files stay excluded.

**Pull all** runs ordinary `git pull` for each project using existing Git configuration, credentials and credential helpers. It attempts pulls with local changes, without stashing. One failure does not stop the remaining projects. Results appear on the dashboard and in **Git & Task Output**. Git actions are serialized per project. Vibedev stores no credentials.

Build locations such as `Program.cs(12,4)` or `Program.cs:12:4` are clickable in the terminal. Ctrl+click or double-click them in **Git & Task Output**. Navigation opens the corresponding registered project, file, line and column. Relative build paths use the task working directory or the trailing project path in MSBuild errors.

## Run configurations

The run dropdown reads only `.vscode/launch.json`, including comments and trailing commas. Refresh it after editing configurations. Without launch configurations, Run is disabled.

Supported .NET configurations:

- `coreclr` / `clr`, `request: launch`: `program` (DLLs run through `dotnet`, executables directly), `args`, `cwd`, `env` and `preLaunchTask`.
- `dotnet`, `request: launch`: `projectPath`, optional `launchSettingsProfile`, `args`, `cwd`, `env` and `preLaunchTask`. If `projectPath` is absent, exactly one `.csproj` must exist directly in the root. `dotnet run` uses the project's standard launch settings.
- Variables: `${workspaceFolder}`, `${workspaceFolderBasename}`, `${env:NAME}`, `${pathSeparator}` and `${/}`.
- Prelaunch tasks from `.vscode/tasks.json`: `process` and `shell`, string arguments, `options.cwd`, `options.env` and task dependencies, executed in dependency order. Shell tasks use Windows PowerShell. Background tasks, extension-provided task types, custom shells and task Windows overrides report unsupported errors.

Attach, other runtime types, unresolved extension/input variables, launch Windows overrides, `envFile`, `pipeTransport` and `postDebugTask` report explicit errors. Debugger-only settings such as `stopAtEntry`, `justMyCode` and `serverReadyAction` do not affect Run. Browser launching is not implemented. Use `dotnet` configurations for launchSettings profiles. Use launch configurations you trust: they execute local programs.

One terminal is shared across all projects, with one active project run at a time. Output history stays visible. Starting Run replaces the interactive PowerShell process with the project; when it finishes, fresh PowerShell starts in its working directory. Shell variables are not retained across a project run. Stop terminates the run and its process tree, including prelaunch tasks. Keyboard input, ANSI colors, Unicode, resize and Ctrl+C are supported through Windows ConPTY and bundled xterm.js.

Example `.vscode/launch.json`:

```jsonc
{
  "version": "0.2.0",
  "configurations": [
    {
      "name": "My app",
      "type": "coreclr",
      "request": "launch",
      "preLaunchTask": "build",
      "program": "${workspaceFolder}/bin/Debug/net10.0/MyApp.dll",
      "cwd": "${workspaceFolder}",
      "args": [],
      "env": { "ASPNETCORE_ENVIRONMENT": "Development" }
    }
  ]
}
```

Example `.vscode/tasks.json`:

```json
{
  "version": "2.0.0",
  "tasks": [
    {
      "label": "build",
      "type": "process",
      "command": "dotnet",
      "args": ["build", "${workspaceFolder}/MyApp.csproj"],
      "problemMatcher": "$msCompile"
    }
  ]
}
```

## Verification

```powershell
dotnet build Vibedev.slnx -c Release
dotnet test tests/Vibedev.Tests -c Release
dotnet run --project tests/Vibedev.SmokeTests -c Release
dotnet run --project tests/Vibedev.SmokeTests -c Release -- --project-drag
dotnet run --project tests/Vibedev.SmokeTests -c Release -- --dashboard
```

Core tests use disposable local Git remotes and check selective staging/unstaging/committing, renames, Unicode paths, build-location parsing, LSP message framing, commit-all, push/pull, cancellation, text encoding, external-change protection, recursive discovery, project search and launch resolution. Windows smoke tests exercise actual WPF panels, file watching, Git diffs and selection, tab swaps, terminal visibility, C# completion/diagnostics/hover/definitions, clickable xterm errors, layout restoration, WebView2 and ConPTY input/stop. Install C# support with the **C# server** button (or `dotnet tool install --global csharp-ls --version 0.26.0`) before running smoke tests. Screenshots/results are written under `artifacts/`, leaving workspace projects untouched.

Regenerate terminal assets with `npm ci --ignore-scripts` and `npm run build`.

## Components

[AvalonDock](https://github.com/Dirkster99/AvalonDock) supplies docking and layout serialization; [AvalonEdit](https://github.com/icsharpcode/AvalonEdit) supplies editing; [WebView2](https://learn.microsoft.com/en-us/microsoft-edge/webview2/) hosts [xterm.js](https://github.com/xtermjs/xterm.js). Launch handling follows the supported subset of [VS Code C# launch settings](https://code.visualstudio.com/docs/csharp/debugger-settings). License notices are in `THIRD-PARTY-NOTICES.md` and the terminal vendor directory.

## License

Vibedev uses the custom [Vibedev Source-Available License 1.0](LICENSE).
You may use it for commercial work and create and sell your own products with it.
You may study, modify and redistribute Vibedev for free, but you may not sell
Vibedev or modified versions, bundle it with a paid offering, or charge for
access to it as a hosted service without separate written permission. Your own
projects and output are not subject to this license merely because you used
Vibedev to develop them.

The resale restriction makes this source-available software, rather than open
source under the [Open Source Definition](https://opensource.org/osd).
Third-party components retain their own licenses; see [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
