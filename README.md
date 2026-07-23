# Mellow Narrator

Mellow Narrator is a .NET 10 MAUI application for creating and playing durable, LLM-driven interactive stories on Windows and Android.

## Projects

- `Mellow.Narrator.Core` — domain models, use cases, limits, Story Bible processing, and interfaces.
- `Mellow.Narrator.OpenAiCompatible` — non-streaming Chat Completions adapter with structured JSON output and retry handling.
- `Mellow.Narrator.Persistence` — versioned folder-of-JSON persistence, backups, recovery, staging, copying, and trash.
- `Mellow.Narrator.Gui` — standard MAUI `TabbedPage` UI and secure-storage adapter.
- `Mellow.Narrator.Cli` — unreleased manual test harness that requires an isolated data directory.
- `Mellow.Narrator.Tests` — Core unit tests plus provider and persistence integration tests.

The complete behavioral and architectural specification is in [Plan.md](Plan.md).

## Build and test

```powershell
dotnet restore Mellow.Narrator.slnx
dotnet test Mellow.Narrator.Tests/Mellow.Narrator.Tests.csproj
dotnet build Mellow.Narrator.Gui/Mellow.Narrator.Gui.csproj -f net10.0-windows10.0.19041.0
dotnet build Mellow.Narrator.Gui/Mellow.Narrator.Gui.csproj -f net10.0-android
```

The CLI never opens the GUI data directory:

```powershell
dotnet run --project Mellow.Narrator.Cli -- --data C:\Temp\MellowNarratorTest list
```
