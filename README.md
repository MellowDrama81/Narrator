# Mellow Narrator

Mellow Narrator is a .NET 10 MAUI application for creating and playing durable, LLM-driven interactive stories on Windows and Android. Application-wide LLM prompt templates and rolling structured logging are configurable from Settings.

## Projects

- `Mellow.Narrator.Core` — domain models, use cases, limits, Story Bible processing, and interfaces.
- `Mellow.Narrator.OpenAiCompatible` — non-streaming Chat Completions adapter with structured JSON output and retry handling.
- `Mellow.Narrator.Persistence` — versioned folder-of-JSON persistence, backups, recovery, staging, copying, and trash.
- `Mellow.Narrator.Gui` — standard MAUI `TabbedPage` UI and secure-storage adapter.
- `Mellow.Narrator.Cli` — unreleased manual test harness that requires an isolated data directory.
- `Mellow.Narrator.Tests` — Core unit tests plus provider and persistence integration tests.

The complete behavioral and architectural specification is in [Plan.md](Plan.md).

## Example Story Definition

[You are Syknet-definition.json](examples/You%20are%20Syknet-definition.json) is an example exported Story Definition.
To use it, open the **Story Definitions** page in Mellow Narrator, select **Import**, and choose the downloaded JSON file.

## Build and test

```powershell
dotnet restore Mellow.Narrator.slnx
dotnet test Mellow.Narrator.Tests/Mellow.Narrator.Tests.csproj
dotnet build Mellow.Narrator.Gui/Mellow.Narrator.Gui.csproj -f net10.0-windows10.0.19041.0
dotnet build Mellow.Narrator.Gui/Mellow.Narrator.Gui.csproj -f net10.0-android
```

## Windows distribution

The Windows project pins Windows App SDK `1.8.260508005` and sets
`WindowsAppSDKSelfContained=true`. This keeps the tested Windows UI/input runtime with the application and avoids
depending on a different shared Windows App SDK runtime installed on a user's computer.

Publish .NET itself as self-contained as well:

```powershell
dotnet publish Mellow.Narrator.Gui/Mellow.Narrator.Gui.csproj `
  -c Release `
  -f net10.0-windows10.0.19041.0 `
  -r win-x64 `
  --self-contained true
```

Distribute the complete publish directory through an installer, MSIX, or ZIP; distributing only the executable will
omit required native DLLs. Produce and test a separate `win-arm64` publish when native Windows on ARM support is
required.

Keep the Windows App SDK version explicitly pinned and update it deliberately after testing newer servicing releases.
Before releasing, smoke-test the published artifact on clean supported Windows 10 and Windows 11 machines, including
mouse, touch, scrolling, tab reordering, and story interaction.

See Microsoft's documentation for
[self-contained Windows App SDK deployment](https://learn.microsoft.com/windows/apps/package-and-deploy/self-contained-deploy/deploy-self-contained-apps)
and [deployment-model trade-offs](https://learn.microsoft.com/windows/apps/package-and-deploy/deploy-overview).

The CLI never opens the GUI data directory:

```powershell
dotnet run --project Mellow.Narrator.Cli -- --data C:\Temp\MellowNarratorTest list
```
