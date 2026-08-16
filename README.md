# Mellow Narrator

Mellow Narrator is available as both a .NET 10 MAUI application for Windows and Android and a
client-side Angular web application. Both versions create and play durable, LLM-driven interactive
stories. You describe a premise, the LLM turns it into a structured Story
Definition (a refined premise plus a Story Bible of durable facts), and from there you play through the
story turn by turn: you describe what your character does, the LLM narrates what happens next and offers
a few suggested actions, and the Story Bible is kept up to date automatically as the plot develops. Story
Definitions and Story States are saved locally and durably, independent of any specific LLM provider, so
you can switch providers or models mid-story without losing anything.

Mellow Narrator talks to any OpenAI-compatible Chat Completions endpoint — the hosted OpenAI API, Azure
OpenAI, or a local server such as Ollama, LM Studio, or text-generation-webui in OpenAI-compatible mode.
Application-wide LLM prompt templates and rolling structured logging are configurable from Settings.

## Core concepts

- **Story Definition Prompt** — the source material you enter when generating a Story Definition. It
  can describe every aspect of the desired definition: premise, characters, starting state, tone,
  mutable facts, intended developments, and possible outcomes. The generator organizes that material
  into the appropriate Story Definition fields. It is not the Story Prompt stored in the result.
- **Story Definition** — the reusable template for a story: a title, a refined Story Prompt (the
  immutable setting, premise, tone, and narration rules that are sent with every request for the entire
  story), an optional Initial Events prompt (guidance used only for the earliest scenes, then dropped),
  an initial Story Bible, secret Planned Events, and victory/loss conditions. You can start any number
  of independent Story States from the same definition.
- **Story Bible** — the durable, structured memory for a story: a set of named entries, each with a
  category, an importance level from 1 (least) to 5 (most), and two lists of short facts —
  **known facts** (things the player character already knows or could plainly observe) and
  **secret facts** (hidden facts — schemes, true motives — the character doesn't know yet, but the
  narrator does, for consistency). As the story progresses, the LLM proposes incremental updates to the
  Bible (add, replace, or remove an entry) and moves facts from secret to known as the character
  genuinely learns them in-story. If a Story Bible grows past its configured size limits, the
  least-important, least-recently-relevant entries are culled automatically (with your confirmation) to
  make room.
- **Story State** — one playthrough of a Story Definition: its own copy of the Story Bible (which
  diverges from the definition's as the story evolves) plus the full turn-by-turn history of player
  actions and narration. You can have several Story States running from the same Story Definition at
  once, and you can branch a Story State at any point with **Copy Story**.
- **Turn** — one exchange: the player's action (blank for the opening scene) and the LLM's narration,
  suggested actions, and any Story Bible updates that resulted from it.

## Getting started

1. Open **Settings** (one of the three fixed tabs) and configure the API connection — see
   [Configuring the LLM connection](#configuring-the-llm-connection) below — then **Save**.
2. Open the **Definitions** tab and either:
   - click **New**, enter a Story Definition Prompt describing everything the generated definition
     should contain, and click **Generate Story Definition**. The LLM organizes it into a title, a
     polished immutable Story Prompt, an Initial Events prompt, a starting Story Bible, Planned Events,
     and victory/loss conditions; or
   - click **New** and then **Create Blank Definition** to bypass generation and open an empty,
     persisted definition for manual authoring; or
   - click **Import** and choose an exported `*-definition.json` file — for example,
     [The Awakening-definition.json](examples/The%20Awakening-definition.json), an example
     Story Definition included in this repository.
3. Review the generated, blank, or imported definition — you can edit the title, Story Prompt, Initial
   Events, and Story Bible entries directly — then click **Start Story**.
4. You're now on a **Play Story** tab: type what your character does into the action box (or click one
   of the suggested actions) and click **Submit**. Repeat for as long as you like.

## Configuring the LLM connection

All connection settings live under **Settings → API Connection**:

- **Base URL** — the root of an OpenAI-compatible API, *without* a trailing path segment like
  `/chat/completions`. Examples: `https://api.openai.com/v1` for OpenAI, or `http://localhost:11434/v1`
  for a local Ollama/LM Studio/text-generation-webui server running in OpenAI-compatible mode. A base
  URL that already has a query string (as some Azure OpenAI deployment URLs do) is preserved correctly.
- **Model ID** — enter one directly, or click **Load Models** first to query the provider's `/models`
  endpoint and choose from a dropdown. Changing the model applies to every subsequent request, including
  requests made from stories that are already in progress.
- **API key** — optional (leave it blank for a local server that doesn't require one). Once saved, it's
  stored using the operating system's secure credential storage (Credential Locker on Windows, Keystore
  on Android) — never in a plain settings file, and never written to the logs regardless of log level.
  A masked placeholder means a key is already stored; focus the field to type a replacement, or use
  **Clear stored API key** to remove it.
- **Test Connection** — saves your current settings, then probes the provider with progressively less
  demanding request styles (strict JSON Schema, JSON mode, then a plain prompted-JSON fallback for
  providers without native structured output) and reports which one worked. Do this once after changing
  the base URL or model, since a story generation request retries only once on a malformed response and
  benefits from Mellow Narrator already knowing which structured-output style your provider supports.

**Generation** settings, also under Settings, control how each request is made and how much of the
story is sent as context: request timeout, maximum output tokens, temperature, top-p, reasoning effort
(for reasoning models that support it — leave blank to use the provider's default), how many recent
turns of narration are included as context on each request, and the maximum number of Story Bible
entries a definition or state is allowed to hold. The collapsible **Story Bible & Retries** and
**Content Limits** sections expose finer limits (character limits per field, automatic retry/backoff
behavior on transient HTTP errors, number and length of suggested actions, narration paragraph/sentence
counts, and more) — each field shows its default and valid range beneath it, and **Reset defaults**
restores every setting on the page to its shipped defaults in one click.

**Logging** (collapsible, under Settings) controls a rolling JSON-lines log written to the app's private
data folder. The default level, Information, is safe to leave on. **Trace** additionally records
complete LLM request and response bodies — full Story Bibles, player actions, and narration — so only
enable it while diagnosing a specific problem; API credentials are excluded at every log level.

## Web application

`Mellow.Narrator.Web` is a completely client-side Angular 22 application using Angular Material and
TypeScript. It provides the core definition, Story Bible, story-playing, import/export, copying, and
trash workflows in a browser. There is no application backend: LLM requests are sent directly from the
browser to the configured OpenAI-compatible endpoint, and settings, definitions, stories, turns, drafts,
and trash are persisted in IndexedDB.

Browser storage is scoped to the page's origin. Data created at `http://localhost:4200` is therefore
separate from data stored by a deployed copy of the application. Use the JSON import/export controls to
move definitions or stories between installations.

### Run the web application locally

Prerequisites:

- Node.js `22.22.3` or later in the Node 22 line, Node.js `24.15.0` or later, or Node.js 26+.
- pnpm 11.9.0. Corepack is the recommended way to activate the version recorded by the project.
- .NET 10 SDK, used by the shared prompt-template generator before web development, tests, and builds.

From the repository root:

```powershell
cd Mellow.Narrator.Web
corepack enable
corepack prepare pnpm@11.9.0 --activate
pnpm install --frozen-lockfile
pnpm start
```

Open [http://localhost:4200](http://localhost:4200). The development server reloads the page when source
files change.

To run the web tests or create an optimized production bundle:

```powershell
pnpm test
pnpm run build
```

The production output is written under `Mellow.Narrator.Web/dist/`.

### GitHub Pages deployment

The Angular application is published from this repository at
[https://mellowdrama81.github.io/Narrator](https://mellowdrama81.github.io/Narrator). Pushes to `master`
automatically build and deploy it through `.github/workflows/pages.yml`. To create the same static Pages
artifact locally from `Mellow.Narrator.Web`, run:

```powershell
pnpm run build:github-pages
```

The deployable files are written to `Mellow.Narrator.Web/dist/github-pages/`. This build uses the
`/Narrator/` base path, replaces social-card placeholders with the full production site URL, adds
`.nojekyll`, and creates `404.html` as an Angular routing fallback. The `Narrator` repository's Pages
publishing source must be set to **GitHub Actions**.

### Configure an LLM in the browser

Open **Settings**, enter the provider's base URL and API key, select **Load models**, choose a model from
the resulting list, then select **Save settings** or **Test connection**. The API key is stored in that
browser profile's IndexedDB, so use the web application only from a trusted browser profile and device.

Because requests originate in the browser, the provider must allow cross-origin requests from the web
application's origin. For local development, allow `http://localhost:4200`. A local OpenAI-compatible
server must also be reachable by the browser; an HTTPS-hosted deployment may be prevented by browser
mixed-content rules from calling an unsecured HTTP endpoint.

## Shared prompt templates

The .NET and web applications use the same LLM prompt templates. The canonical source is the `prompts/`
directory:

- `manifest.json` maps each template key to a Markdown file and declares the placeholders that the
  template may contain.
- The `.md` files contain the prompt text. Edit these files when changing a prompt; do not edit either
  generated source file directly.

The generator validates that every placeholder in a prompt is declared in the manifest, then produces
the language-specific constants used by each application:

- `Mellow.Narrator.Core/Generated/PromptTemplates.g.cs` for .NET.
- `Mellow.Narrator.Web/src/app/core/prompt-templates.generated.ts` for Angular.

Generation is part of both build processes. Building `Mellow.Narrator.Core` runs the C# generator
incrementally before compilation. In the web project, the `prestart`, `pretest`, and `prebuild` hooks
regenerate the TypeScript output before `pnpm start`, `pnpm test`, and `pnpm run build`.

To regenerate both outputs explicitly from the repository root:

```powershell
dotnet run --project tools/Mellow.Narrator.PromptGenerator -- --root . --target all
```

To verify that the checked-in generated files match the canonical templates without modifying files:

```powershell
dotnet run --project tools/Mellow.Narrator.PromptGenerator -- --root . --target all --check
```

The equivalent check from `Mellow.Narrator.Web` is `pnpm prompts:check`. Use `--target csharp` or
`--target typescript` instead of `--target all` when only one output is needed. Commit changes to the
canonical prompt files, the manifest when applicable, and the regenerated outputs together.

## Using the app

The app window is a set of tabs. Three are fixed and always present — **Settings**, **Definitions**, and
**Stories** — plus any number of tabs you open for editing a specific definition or playing a specific
story. The toolbar on every page has **Manage Tabs** (reorder or review your open dynamic tabs) and,
on dynamic tabs, **Close**. Your open tabs, their contents (including an unsaved draft or a pending
player action), and even an in-progress LLM request that gets interrupted by closing the app are all
saved automatically and restored the next time you open Mellow Narrator, with an offer to retry
whatever was interrupted.

### Definitions tab

Lists every Story Definition you've created or imported. **New** opens a blank Story Definition Prompt
drafting tab;
**Open** opens the selected definition for editing; **Start** begins a new Story State from the selected
definition without leaving the list. **Earlier**/**Later** reorder the list. **Import**/**Export**
read and write a Story Definition as a single JSON file. **Delete** moves a definition to Trash — any
Story States already started from it keep playing normally, since each has its own independent copy of
the Story Bible.

Opening a definition shows its title, Story Prompt, and Initial Events prompt as editable fields (click
**Save Definition** to keep changes), its Story Bible, and buttons to **Start Story** or **Export** it.

### Playing a story

A Play Story tab shows the narration so far, the current suggested actions as clickable buttons, and a
text box for typing your own action instead. The book icon in the top-right corner opens a side panel
with the story's current Story Bible. While a request is in flight, the page is covered by a
translucent "Writing…" overlay and controls are disabled until the LLM responds; if a request fails,
you're offered a retry with the same action.

- **Copy Story** branches the story: it creates an independent copy of the current Story State (Bible
  and full history included) and opens it in a new tab, leaving the original untouched.
- **Export** saves the complete Story State (Bible and turn history) as a JSON file that can be
  re-imported later, on this device or another. **Export Full History** saves just the narration as
  readable plain text. **Export Bible History** (in the side panel) saves a plain-text log of every
  Story Bible change and why it happened.

### Stories tab

Lists every Story State. **Open** switches to (or opens) its Play Story tab. **Label** lets you rename
it to something more memorable than the default. **Copy** branches it without opening the source first.
**Earlier**/**Later** reorder the list. **Import**/**Export** work the same way as on the Definitions
tab. **Delete** closes any open tab for that story and moves it to Trash.

### Story Bible editor

Wherever a Story Bible is shown (a definition's initial Bible, or a story's current Bible), you get a
search box, and filters by category and importance, plus **Add Entry**. Clicking an entry's title
expands it for editing: category, name, known facts, secret facts (one per line each), and importance;
**Save** applies changes immediately, **Remove** deletes the entry after confirmation. Each entry also
shows its last-relevant turn number, so you can see at a glance which entries the LLM still considers
active in the story versus ones that have gone stale and may eventually be culled.

### Trash

Settings → **Manage Trash** opens a list of everything moved to Trash (deleted Story Definitions and
Story States). **Restore** puts an item back where it was; **Delete Permanently** or **Empty Trash**
remove it for good — both actions ask for confirmation first, since they cannot be undone.

## Projects

- `Mellow.Narrator.Core` — domain models, use cases, limits, Story Bible processing, and interfaces.
- `Mellow.Narrator.OpenAiCompatible` — non-streaming Chat Completions adapter with structured JSON output and retry handling.
- `Mellow.Narrator.Persistence` — versioned folder-of-JSON persistence, backups, recovery, staging, copying, and trash.
- `Mellow.Narrator` — MAUI Blazor Hybrid UI using the same Core, provider, and persistence services.
- `Mellow.Narrator.Web` — client-side Angular Material UI with IndexedDB persistence.
- `prompts` — canonical Markdown prompt templates and their manifest, shared by both applications.
- `tools/Mellow.Narrator.PromptGenerator` — build-time generator for typed C# and TypeScript prompt constants.
- `Mellow.Narrator.Tests` — Core unit tests plus provider and persistence integration tests.

## Build and test

```powershell
dotnet restore Mellow.Narrator.slnx
dotnet test Mellow.Narrator.Tests/Mellow.Narrator.Tests.csproj
dotnet build Mellow.Narrator/Mellow.Narrator.csproj -f net10.0-windows10.0.19041.0
dotnet build Mellow.Narrator/Mellow.Narrator.csproj -f net10.0-android
```

Every successful push to `master` also publishes the Android app in the Release configuration and
uploads its signed APK to the **Build and test** workflow run as an artifact named
`Mellow-Narrator-Android-<commit SHA>`. Artifacts are retained for 30 days. The automated build uses
the Android development signing identity supplied by the CI runner, so it is suitable for testing and
sideloading, but it is not a production release identity. Configure a securely backed-up release
keystore in GitHub Actions before treating these APKs as upgradeable production distributions.

## Windows distribution

The Windows project pins Windows App SDK `1.8.260508005` and sets
`WindowsAppSDKSelfContained=true`. This keeps the tested Windows UI/input runtime with the application and avoids
depending on a different shared Windows App SDK runtime installed on a user's computer.

Publish .NET itself as self-contained as well:

```powershell
dotnet publish Mellow.Narrator/Mellow.Narrator.csproj `
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
