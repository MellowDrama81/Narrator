# Title
Mellow Narrator

# Description
A .Net MAUI application which allows the user to define and play LLM-driven interactive stories.
The application will connect to an OpenAI-compatible API to use the LLM.

# Solution Structure
Mellow.Narrator: VisualStudio solution
- Mellow.Narrator.Gui: C# .Net 10 MAUI application targeting Windows and Android
- Mellow.Narrator.Core: C# .Net 10 Class Library
- Mellow.Narrator.OpenAiCompatible: C# .Net 10 Class Library
- Mellow.Narrator.Persistence: C# .Net 10 Class Library
- Mellow.Narrator.Cli: C# .Net 10 Console Application
- Mellow.Narrator.Tests: C# .Net 10 Unit and Integration Test project

Mellow.Narrator.Gui should contain UI code, the application composition root and MAUI-specific platform adapters only.

Mellow.Narrator.Core should contain the domain and application logic and the interfaces required from persistence and external providers.

Mellow.Narrator.OpenAiCompatible should implement the Core LLM-provider interfaces using the OpenAI-compatible HTTP contract in this plan. It contains HTTP request and response transport models, capability probing, structured-output handling, retry policy and provider-error mapping. It references Core and must not reference GUI, MAUI or Persistence.

Mellow.Narrator.Persistence should implement the Core persistence interfaces using the versioned JSON folder layout. It references Mellow.Narrator.Core; Mellow.Narrator.Core must not reference Mellow.Narrator.Persistence.

Mellow.Narrator.Cli is an unreleased developer tool for manually testing Core and the OpenAI-compatible provider adapter. It uses Persistence only with an explicitly selected isolated test-data directory and must never open the GUI application's data directory.

Mellow.Narrator.Tests contains unit and integration tests for Mellow.Narrator.Core, Mellow.Narrator.OpenAiCompatible and Mellow.Narrator.Persistence.

Do not create a plain .NET unit-test project for the GUI. Platform-independent presentation and workspace rules belong in Core and are unit tested in Mellow.Narrator.Tests. When target-specific Windows and Android UI automation is introduced, it must use a real MAUI-capable device automation harness and execute the application on those targets.

# OpenAI-Compatible API Contract

## API Connection

The initial version supports exactly one application-wide API connection. It does not support named connection profiles or switching between multiple connections.
The API connection contains a base URL (including any `/v1` prefix), an optional API key, a model ID, a request timeout and the capabilities recorded by the connection test.
When an API key is configured, send it using the `Authorization: Bearer <key>` header.
The application is the source of truth for conversation history and Story States. Do not depend on provider-hosted conversation state.
Use the model currently selected in Settings for every LLM operation, including operations on existing Story States. Story Definitions and Story States do not pin or override the model.
Changing the selected model affects all requests started after the change. Capture the selected model when a request begins, and record that model on any successfully completed turn.
Do not silently fall back to another model when the selected model is unavailable.

## Baseline Provider Contract

Use the Chat Completions API as the initial compatibility target.
Send generation requests as JSON to `POST {baseUrl}/chat/completions` using developer or system instructions, plus user and assistant messages. Prefer the modern `developer` role and `max_completion_tokens` field. During connection testing, fall back through the system role and legacy `max_tokens` field when required by the selected compatible provider, and persist the working combination as detected capabilities.
Use non-streaming responses so a complete structured response can be validated before any state is displayed or persisted.
Attempt model discovery using `GET {baseUrl}/models`. Model discovery is optional; allow the user to enter a model ID manually when the endpoint is unavailable.
The Responses API may be supported by a separate provider adapter in a future version.

Define the provider abstraction consumed by application services in `Mellow.Narrator.Core`. Implement it in `Mellow.Narrator.OpenAiCompatible` and provide an `AddMellowNarratorOpenAiCompatible(IServiceCollection)` registration extension using a typed `HttpClient`. GUI and CLI use the same adapter; provider HTTP details must not leak into Core use cases.

## Connection Testing and Capabilities

Provide a connection test which:
- Validates the base URL and authentication.
- Attempts to load the model list.
- Confirms that the selected model can generate a minimal response.
- Probes strict JSON Schema Structured Outputs, then JSON mode and finally prompted JSON.
- Records the supported structured-output tier for the API connection.
- Records whether the selected provider/model accepts `max_completion_tokens` or legacy `max_tokens`, and developer or system instruction messages.
- Reports authentication, endpoint, model and structured-output failures separately.

A provider is usable only if it can produce responses which pass the application's schema and domain validation.
The model list does not provide model capability or supported-parameter metadata. Always allow the maximum output length to be configured. Expose temperature, top-p and reasoning effort as optional settings.
Do not probe parameters individually. Send an optional parameter only when the user has configured it. If the selected model rejects a configured parameter, do not retry automatically; identify the unsupported parameter and allow the user to clear or change it.

## Structured Responses

Use a defined response schema for player-answer warning checks, Story Definition generation (including its initial Story Bible), opening-scene generation and each story turn.
Prefer strict JSON Schema Structured Outputs. If it is unavailable, use JSON mode. If JSON mode is also unavailable, prompt the model to return JSON, validate it locally and retry once with the validation errors.
Treat a refusal as a failed operation rather than as malformed structured output.

A story-turn response contains narration, suggested player actions, the IDs of existing Story Bible entries relevant to the turn and a list of updates to apply to the Story Bible:

```json
{
  "narration": "You enter the abandoned observatory...",
  "suggestedActions": [
    "Examine the telescope",
    "Search the astronomer's desk"
  ],
  "relevantStoryBibleEntryIds": [
    "55e062f8-4814-47f6-8150-94709e07c5ec"
  ],
  "storyBibleUpdates": [
    {
      "operation": "add",
      "entryId": null,
      "entry": {
        "category": "location",
        "name": "Abandoned Observatory",
        "content": "An old observatory overlooking the northern valley.",
        "importance": 3
      }
    },
    {
      "operation": "replace",
      "entryId": "9a1e7f54-9f87-4b8d-836a-018dc1bf327a",
      "entry": {
        "category": "character",
        "name": "Elena",
        "content": "Elena now distrusts the player after discovering the stolen map.",
        "importance": 4
      }
    },
    {
      "operation": "remove",
      "entryId": "eae4f260-af0f-4c99-af31-c90991cddb25",
      "entry": null
    }
  ]
}
```

Every existing Story Bible entry has a stable ID, an LLM-assigned importance from 1 through 5 and the sequence number of the turn when it was last relevant. Supply all of this metadata to the LLM.
Every opening-scene and story-turn response contains `relevantStoryBibleEntryIds`, listing the existing entries which were relevant to that turn.
An `add` update supplies an entry without an ID; the application assigns its stable ID.
A `replace` update references an existing entry ID and supplies the complete replacement entry.
A `remove` update references an existing entry ID and does not supply an entry.
A response must not update the same existing entry more than once. Unknown IDs, duplicate updates, unknown relevant-entry IDs and invalid entries cause validation failure and one corrective retry.
Apply updates to a copy of the current Story Bible first. Set `LastRelevantTurnNumber` to the current turn number for every entry flagged as relevant. Entries added or replaced during the turn are automatically relevant and receive the current turn number even if the LLM does not flag them.
An entry which is neither flagged, added nor replaced retains its existing importance and last-relevant turn. An entry removed in the same response must not be flagged as relevant.
Persist the player action, narration, suggested actions, complete relevant-entry ID list, update list, automatic culls and resulting Story Bible atomically only after every update has been validated and applied successfully.
Retain the applied update list with the story turn as an audit history while keeping the resulting Story Bible materialized for efficient loading.

## Request Context Construction

Every story-generation request contains the Story Prompt, the player's final setup responses, the complete current Story Bible with each entry's importance and last-relevant turn number, the configured number of most recent completed turns and the current player action.
For recent turns, include the player action and narration. The opening turn may be included when it falls within the configured recent-turn count; suggested actions from previous turns do not need to be sent.
The recent-turn count is a non-negative setting. Treat it as the maximum number of completed turns to include, since a new story may contain fewer turns.
Do not summarize older turns. Retain the complete turn history in local persistence, but omit turns older than the configured recent-turn window from generation requests.
Always send the complete Story Bible. Never truncate it, select only some entries or replace it with a summary.

## Story Bible Size Control

Enforce application-level limits for the maximum number of Story Bible entries, the maximum serialized length of an individual entry and the maximum total serialized Story Bible size. Use deterministic character-based limits because tokenization differs between compatible providers.
Measure these limits against the active materialized `StoryBible` only; audit and maintenance history is not part of the Bible sent to the LLM and does not count toward these limits.
Use the positive `MaxStoryBibleEntries` setting as the target maximum entry count after applying a proposed update list and updating relevance.
Validate the current Bible before sending a request and validate the proposed resulting Bible before committing a turn. Show a non-blocking warning when a Bible approaches its configured limits.
If an existing Story Definition or Story State Bible exceeds any current limit, block story generation and show an explicit warning with its current and allowed entry counts, largest-entry length and total serialized length. Offer:
- **Increase Limits**, which opens Settings without changing the Bible and rechecks it after the user saves.
- **Automatically Cull**, which previews the entries that the deterministic local policy will remove and requires confirmation before changing the Bible.
- **Cancel**, which makes no changes.

Saving lower limits does not silently modify existing Bibles. The Settings Page warns when the new values would make existing Bibles nonconforming; each affected Bible must be resolved explicitly before it is next used for generation.
Automatic limit culling does not call the LLM. First remove every entry which individually exceeds `MaxStoryBibleEntryCharacters`. Then, while the entry-count or total-size limit is exceeded, remove entries by lowest importance, oldest `LastRelevantTurnNumber` and ascending entry ID. The preview uses this exact order and shows the complete proposed removal set.
Apply a confirmed cull atomically to only the selected Story Definition or Story State. Record its limits and applied removals in that aggregate's Story Bible maintenance history. A Story Definition cull updates `UpdatedAtUtc`; a Story State cull does not create a story turn or change `LastActionAtUtc`.
Prompt the LLM to keep only durable story facts in the Bible, prefer replacing existing entries over adding duplicates, remove obsolete entries and consolidate related entries without losing required details.
After applying the LLM updates and relevance flags, automatically cull entries until the count is no greater than `MaxStoryBibleEntries`.
Cull the lowest-importance entries first. Within the same importance, cull the entries with the oldest `LastRelevantTurnNumber` first. For a remaining tie, compare entry IDs in ascending ordinal order and cull the lowest first.
Record automatic culls as applied Story Bible removal changes so they remain auditable and replayable.
For per-entry-length or total-size violations, do not commit the proposed result. Retry once with validation feedback requiring the model to replace, remove or consolidate entries so the resulting Bible fits within those limits.
If the corrective retry still violates a non-count size limit, fail the operation, preserve the previous Story State and explain the size problem to the user.
Do not silently omit Bible entries to fit a provider context window. If a provider rejects a request for context length, preserve the Story State and advise the user to reduce the recent-turn count. The application-level Bible limits should prevent the Bible itself from reaching this condition.

## Request Execution and Failure Handling

Allow the user to cancel a request and permit only one in-flight request for a given Story State.
An in-flight request belongs to the tab which started it and is never persisted or resumed after process recreation.
Retry rate-limit and server failures up to `RetrySettings.MaxAutomaticRetries` times after the initial attempt. Use exponential backoff starting at `InitialDelay`, cap it at `MaxDelay` and add up to 20% positive jitter.
When `Retry-After` is supplied and does not exceed `MaxRetryAfter`, wait for that duration instead of the calculated backoff. If it exceeds the configured maximum, stop automatic retries and tell the user when the provider says to retry. Cancellation interrupts both HTTP attempts and backoff waits.
The timeout applies separately to each HTTP attempt. A structured-response corrective retry is a new logical request with its own transport-retry allowance and does not consume the preceding request's retry count.
Do not automatically retry authentication, invalid-model or unsupported-parameter errors.
If a request, response validation or Story Bible update fails, preserve the previous Story State and present an actionable error to the user.
Do not write API credentials or full prompts to ordinary logs.

## Retry, Regeneration, Undo and Branching

Do not persist any part of an LLM operation until its complete response has been validated and successfully converted into domain changes.
After automatic retries are exhausted, allow the user to retry a failed operation manually or cancel it. Preserve the Story Prompt draft, Start Story answers or pending player action needed to make the retry.
A manual retry is a new request and uses the model and settings currently selected when that retry begins.
For a failed player action, do not append a turn, apply Story Bible updates, change suggested actions or update `LastActionAtUtc`. Keep the entered action available so the user can retry it or edit it before trying again.

Completed turns are immutable in the initial version. Do not provide regeneration or undo for a successfully committed turn.
Use Story State copying as the deliberate branching mechanism. Allow a copy to be created from either the Play Story List Page or the current Play Story Page.
Create the branch from a transactionally consistent copy of the complete current Story State. Duplicate its label, source Story Definition reference and snapshot, player responses, start and last-action timestamps, complete turn history, player actions, narration, suggested actions, generation metadata, applied Story Bible changes, Story Bible maintenance history and current Story Bible.
Give the copy a new top-level identity, remap nested technical IDs consistently and preserve every internal reference. Append it to the Story State list and open it in a new Play Story Page.
The original and copied Story States evolve independently after the copy. Do not allow a copy operation while an LLM request for the source Story State is in flight.
The stored before-and-after Story Bible changes may support regeneration or undo in a future version, but those features are outside the initial scope.

## In-Flight Request Lifecycle

Persist the input required to retry an operation before starting its LLM request. This includes Story Prompt drafts, Start Story answers and pending player actions.
If the user attempts to close a tab with an in-flight request, offer to cancel the request and close the tab or keep the tab open. Do not close the tab until cancellation has completed.
If a cancellation or interruption is observed before the operation's atomic commit point, discard its response and staged output and restore the last known good durable state. Persistence must never roll an interrupted operation forward from an uncommitted turn or temporary file.
Keep the final atomic publish section short and non-cancellable. If its commit point succeeds before the process stops, the operation is fully committed rather than interrupted and the new valid state is visible when reopened.
When the application moves to the background on Android, cancel all in-flight LLM requests, persist the open-tab state and retain the input required for manual retry. On resume or process recreation, show that the operation was interrupted and offer Retry or Cancel.
Closing a Start Story Page which contains any answers requires confirmation before its temporary progress is discarded.
Closing a Play Story Page which contains a pending or failed player action requires confirmation before that action is discarded. Closing the tab never deletes its durable Story State.
Changing Settings while a request is in flight does not alter that request. Each retry captures the current Settings when the new request begins.

## Initial Scope Exclusions

The initial OpenAI-compatible provider contract does not include Azure OpenAI-specific URLs or authentication, provider-specific tool APIs, server-hosted conversation state, function calling, streaming, images, audio, realtime APIs or arbitrary custom authentication headers.

# Data Models

## Story Definition

A Story Definition is a durable, reusable template for starting stories.

```csharp
public sealed record StoryDefinition(
    Guid Id,
    string Title,
    string StoryPrompt,
    IReadOnlyList<PlayerQuestion> PlayerQuestions,
    StoryBible InitialStoryBible,
    IReadOnlyList<StoryBibleMaintenanceRecord> StoryBibleMaintenanceHistory,
    int SortOrder,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

public sealed record PlayerQuestion(
    Guid Id,
    string Question,
    string ValidationInstruction,
    int SortOrder);

public sealed record StoryBible(
    IReadOnlyList<StoryBibleEntry> Entries);

public sealed record StoryBibleEntry(
    Guid Id,
    string Category,
    string Name,
    string Content,
    int Importance,
    int LastRelevantTurnNumber);
```

Use `Guid` values as stable identifiers. Persist timestamps in UTC and display them in the user's local time.
Keep `StoryBibleEntry.Category` as a validated string so stories can introduce categories which the application has not anticipated.
Validate `StoryBibleEntry.Importance` as an integer from 1 through 5, where 5 is most important.
Use turn zero for the opening scene. Initial Story Bible entries receive `LastRelevantTurnNumber = 0`; cloned initial entries are automatically considered relevant to the opening turn.
The Story Prompt entered by the user is both the seed prompt and the durable Story Prompt; these are not separate values.
When generating a Story Definition, preserve the user-entered title, Story Prompt and player questions as entered. Use the Story Prompt to ask the LLM to generate the initial Story Bible.
The LLM assigns an importance from 1 through 5 to every generated initial Story Bible entry. The application assigns stable IDs to the definition, questions and generated entries and sets their last-relevant turn to zero before persisting them.
If the generated initial Story Bible exceeds any configured Bible limit, apply the same deterministic culling policy before persisting the Story Definition and record the removals as `GeneratedBibleLimitCull` maintenance.
Editing a Story Definition takes place in a temporary Story Prompt Page draft and must not change the durable Story Definition until the user chooses to overwrite it.

When starting a story, take a snapshot of the Story Definition so later edits or deletion of the definition cannot change the Story State:

```csharp
public sealed record StoryDefinitionSnapshot(
    string Title,
    string StoryPrompt,
    IReadOnlyList<PlayerQuestion> PlayerQuestions,
    StoryBible InitialStoryBible);
```

Copy the Story Definition's initial Story Bible into the new Story State, preserve each entry's importance, set its last-relevant turn to zero and assign new IDs to the copied entries so the definition and every Story State have independent entries.
After checking every answer and allowing the player to accept any warnings, ask the LLM for the opening narration, suggested actions and any updates arising from the answers. Use the same incremental Story Bible update format as a normal story turn.
Validate and apply those updates to the copied Bible, then persist the new Story State and its opening turn atomically.

## Story State

A Story State is a durable aggregate containing the current state and complete history of one playable story. It owns an immutable snapshot of the setup used to start it, so it does not depend on the current version or continued existence of its source Story Definition.

```csharp
public sealed record StoryState(
    Guid Id,
    string Label,
    Guid? SourceStoryDefinitionId,
    StorySetupSnapshot Setup,
    StoryBible CurrentStoryBible,
    IReadOnlyList<StoryBibleMaintenanceRecord> StoryBibleMaintenanceHistory,
    int SortOrder,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset? LastActionAtUtc);

public sealed record StorySetupSnapshot(
    StoryDefinitionSnapshot Definition,
    IReadOnlyList<PlayerResponse> PlayerResponses);

public sealed record PlayerResponse(
    Guid QuestionId,
    string Question,
    string ValidationInstruction,
    string Answer);
```

`SourceStoryDefinitionId` records provenance only and may refer to a Story Definition which is later edited or deleted.
`StartedAtUtc` is recorded when the Story State and opening turn are created.
`LastActionAtUtc` remains `null` until the first player action is completed. Thereafter it is the completion time of the most recently persisted player-action turn.

## Story Turns

Store turns as ordered children of a Story State rather than embedding the entire history in the `StoryState` record. This allows recent narration to be loaded without rewriting or loading the complete history.

```csharp
public sealed record StoryTurn(
    Guid Id,
    Guid StoryStateId,
    int SequenceNumber,
    string? PlayerAction,
    string Narration,
    IReadOnlyList<string> SuggestedActions,
    IReadOnlyList<Guid> RelevantStoryBibleEntryIds,
    IReadOnlyList<AppliedStoryBibleChange> StoryBibleChanges,
    DateTimeOffset CompletedAtUtc,
    GenerationMetadata Generation);

public sealed record GenerationMetadata(
    string ModelId,
    string? ProviderResponseId,
    int? InputTokens,
    int? OutputTokens);
```

The opening scene is turn zero and has a `null` `PlayerAction`. Suggested actions belong to the scene which presented them.
Sequence numbers are unique and contiguous within a Story State.
`RelevantStoryBibleEntryIds` stores the complete set marked relevant for the turn after adding the IDs of entries which were added or replaced automatically.
Store the actual model and available provider metadata on every turn so later changes to the active API configuration do not obscure how earlier scenes were generated.
The recorded model is historical metadata only and does not control which model is used for future turns.

## Story Bible Update Models

The structured LLM response contains proposed updates. These are boundary DTOs and must not be persisted directly:

```csharp
public sealed record ProposedStoryBibleEntry(
    string Category,
    string Name,
    string Content,
    int Importance);

public sealed record ProposedStoryBibleUpdate(
    StoryBibleOperation Operation,
    Guid? EntryId,
    ProposedStoryBibleEntry? Entry);

public enum StoryBibleOperation
{
    Add,
    Replace,
    Remove
}
```

After validation, convert each proposal into an applied domain change:

```csharp
public sealed record AppliedStoryBibleChange(
    StoryBibleOperation Operation,
    Guid EntryId,
    StoryBibleEntry? Before,
    StoryBibleEntry? After,
    StoryBibleChangeSource Source);

public enum StoryBibleChangeSource
{
    LlmUpdate,
    AutomaticCull
}

public sealed record StoryBibleMaintenanceRecord(
    Guid Id,
    StoryBibleMaintenanceReason Reason,
    StoryBibleLimitSnapshot Limits,
    IReadOnlyList<AppliedStoryBibleChange> Changes,
    DateTimeOffset CompletedAtUtc);

public sealed record StoryBibleLimitSnapshot(
    int MaxEntries,
    int MaxEntryCharacters,
    int MaxTotalCharacters);

public enum StoryBibleMaintenanceReason
{
    GeneratedBibleLimitCull,
    UserApprovedLimitCull
}
```

For an addition, `Before` is `null` and `After` contains the new entry with its application-assigned ID.
For a replacement, `Before` contains the previous complete entry and `After` contains the complete replacement with the same ID.
For a removal, `Before` contains the removed entry and `After` is `null`.
Changes directly requested by the LLM use `LlmUpdate`. Automatic removals made by the culling policy use `AutomaticCull`.
Use `GeneratedBibleLimitCull` when a newly generated initial Bible must be reduced before its Story Definition is first persisted. Use `UserApprovedLimitCull` for the explicit recovery flow when an existing Bible exceeds current settings.
Storing both sides of an applied change provides a complete audit trail and supports future undo or replay features.

## Story Bible Inspection

Provide a reusable read-only Story Bible inspector for Story Definitions and Story States. Show the active entry count and serialized size, followed by entries grouped by category and ordered by name. Each entry displays its name, content, importance and last-relevant turn number; expose its stable ID in the expanded detail view for diagnostics.

Allow entries to be searched by name or content and filtered by category or importance. Inspection never changes ordering, relevance or durable data.

Provide a separate read-only change-history view:
- For a Story Definition, show its Story Bible maintenance records.
- For a Story State, combine applied changes from committed turns with standalone Story Bible maintenance records and order them newest first.
- For each change, show its operation, source, turn number or maintenance reason, completion timestamp and before/after entry values.

Keep the inspector and history collapsed by default on the Play Story Page so narration remains primary. Load the materialized current Bible from `state.json` immediately, but load older turn-based change history on demand in pages. Do not load the complete turn history merely to display the current Bible.

The initial version does not provide direct manual Story Bible editing. Confirmed automatic limit culling is the only non-LLM operation which changes a Bible.

## Structured LLM DTOs

Use separate DTOs for each structured LLM operation and convert validated results into domain models:

```csharp
public sealed record PlayerAnswerValidationResponse(
    bool HasWarning,
    string? Warning);

public sealed record StoryDefinitionGenerationResponse(
    IReadOnlyList<ProposedStoryBibleEntry> InitialStoryBibleEntries);

public sealed record InitialStoryResponse(
    string Narration,
    IReadOnlyList<string> SuggestedActions,
    IReadOnlyList<Guid> RelevantStoryBibleEntryIds,
    IReadOnlyList<ProposedStoryBibleUpdate> StoryBibleUpdates);

public sealed record StoryTurnResponse(
    string Narration,
    IReadOnlyList<string> SuggestedActions,
    IReadOnlyList<Guid> RelevantStoryBibleEntryIds,
    IReadOnlyList<ProposedStoryBibleUpdate> StoryBibleUpdates);
```

The opening-scene and normal-turn DTOs intentionally use the same response shape but remain distinct types so their different use cases cannot be mixed accidentally.
LLM DTOs contain no durable IDs for newly generated objects. Core assigns IDs only after schema and domain validation succeed.

## Temporary Draft and Workspace Models

Story Prompt and Start Story drafts belong to open-tab state and are not durable domain records.

```csharp
public sealed record StoryPromptDraft(
    Guid? SourceStoryDefinitionId,
    string Title,
    string StoryPrompt,
    IReadOnlyList<PlayerQuestionDraft> PlayerQuestions);

public sealed record PlayerQuestionDraft(
    Guid Id,
    string Question,
    string ValidationInstruction,
    int SortOrder);

public sealed record StartStoryDraft(
    Guid SourceStoryDefinitionId,
    StoryDefinitionSnapshot Definition,
    int CurrentQuestionIndex,
    IReadOnlyList<PlayerAnswerDraft> PlayerAnswers);

public sealed record PlayerAnswerDraft(
    Guid QuestionId,
    string Answer,
    PlayerAnswerValidationStatus ValidationStatus,
    string? ValidationWarning);

public enum PlayerAnswerValidationStatus
{
    NotValidated,
    Valid,
    Warning,
    AcceptedWithWarning
}
```

Ask and check player questions one at a time in their defined order.
Each warning-check request contains the current question, its natural-language validation instruction, the current answer and all preceding answers the player chose to continue with. Do not include unanswered later questions.
If the LLM returns no warning, mark the answer as `Valid` and advance to the next question.
If the LLM returns a warning, show it without blocking progress. Allow the player either to change and recheck the answer or continue with it. Continuing marks the answer as `AcceptedWithWarning` before advancing.
An API, authentication, model, timeout, cancellation or response-validation failure is not a validation warning. Leave the answer `NotValidated`, do not advance and do not provide a Continue Without Validation action. After automatic request retries are exhausted, show the error and allow Retry or Cancel. Retry checks the current answer again; Cancel dismisses the failed request but keeps the answer and current question so the player can retry later, edit it or close the Start Story Page through its normal confirmation flow.
Every player answer must reach `Valid` or `AcceptedWithWarning` before opening-scene generation can begin. This is intentionally blocking because answer validation and narration use the same configured LLM connection.
Persist the current question index, answer text, status and warning as part of the Start Story tab state so the sequential flow can be restored after restart.

Persist the complete set of open tabs as one workspace document:

```csharp
public sealed record WorkspaceState(
    Guid ActiveTabId,
    IReadOnlyList<OpenTabState> Tabs);

public sealed record OpenTabState(
    Guid TabId,
    TabType Type,
    int Position,
    Guid? DurableRecordId,
    StoryPromptDraft? StoryPromptDraft,
    StartStoryDraft? StartStoryDraft,
    PlayStoryTabState? PlayStoryTabState,
    PendingOperationState? PendingOperation);

public sealed record PlayStoryTabState(
    string PendingPlayerAction);

public sealed record PendingOperationState(
    Guid OperationId,
    PendingOperationType Type,
    Guid? TargetRecordId,
    int? ExpectedTurnSequence,
    DateTimeOffset StartedAtUtc);

public enum PendingOperationType
{
    GenerateStoryDefinition,
    ValidatePlayerAnswer,
    GenerateOpeningScene,
    GenerateStoryTurn,
    TestApiConnection
}

public enum TabType
{
    Settings,
    StoryDefinitionList,
    PlayStoryList,
    StoryDefinition,
    StoryPrompt,
    StartStory,
    PlayStory
}
```

Story Definition and Play Story tabs reference their durable records using `DurableRecordId`. Story Prompt and Start Story tabs contain their temporary draft payloads. A Play Story tab also contains its pending player action. The three fixed tabs have no page payload.
Before starting any LLM or connection-test request, persist a `PendingOperationState` with the request's operation ID and enough expected-target information to distinguish an uncommitted operation from one which reached its atomic commit point. Reserve a target record ID before creation requests and record the next expected sequence before story-turn requests.
On startup, run repository recovery before restoring tabs. If the expected durable record or turn reached its commit point, reconcile the tab to that completed result and clear the marker. Otherwise treat the operation as interrupted: keep its user-entered input, clear the marker, restore the last known good aggregate state and offer Retry or Cancel. Never reconstruct or apply the interrupted LLM response.
Use `StoryPromptDraft.SourceStoryDefinitionId` to enforce that no more than one Story Prompt Page may edit a given existing Story Definition. A `null` source ID identifies an independent new-definition draft.
Restore the active tab and tab order on startup. Reject or repair invalid workspace state, including missing durable records, duplicate Play Story tabs for the same Story State and duplicate Story Prompt editor tabs for the same Story Definition.
Write workspace changes atomically and debounce saves while the user is typing. Explicitly closing a draft tab removes its temporary payload after any required confirmation.

## Story Definition Deletion

Require confirmation before deleting a Story Definition.
Before deletion, find every open Story Definition, Story Prompt and Start Story tab associated with that Story Definition.
If an associated Story Prompt editor or Start Story tab is open, require explicit confirmation to discard each tab's temporary draft or progress.
Collect every required confirmation before changing any tab or durable record. If the user declines any required discard or cancellation, leave all tabs and the Story Definition unchanged.
After all confirmations succeed, cancel and complete cancellation of any related in-flight request, close the associated read-only and temporary tabs, move the durable Story Definition and its backup to trash and persist the updated Workspace State.
Do not delete or modify existing Story States created from the Story Definition. Their setup snapshots remain complete and playable, and their `SourceStoryDefinitionId` remains historical provenance even when the source no longer exists.

## API Connection Models

Non-sensitive API settings and detected capabilities are durable configuration. The credential itself is stored only in MAUI Secure Storage.

```csharp
public sealed record ApiConnectionSettings(
    Uri? BaseUrl,
    string? ModelId,
    TimeSpan RequestTimeout,
    int MaxOutputTokens,
    ModelParameters Parameters,
    StoryGenerationSettings StoryGeneration,
    RetrySettings Retry,
    ContentLimitSettings ContentLimits,
    ConnectionCapabilities Capabilities);

public sealed record ModelParameters(
    double? Temperature,
    double? TopP,
    string? ReasoningEffort);

public sealed record StoryGenerationSettings(
    int RecentTurnCount,
    int MaxStoryBibleEntries,
    int MaxStoryBibleEntryCharacters,
    int MaxStoryBibleCharacters,
    int StoryBibleWarningPercent);

public sealed record RetrySettings(
    int MaxAutomaticRetries,
    TimeSpan InitialDelay,
    TimeSpan MaxDelay,
    TimeSpan MaxRetryAfter);

public sealed record ContentLimitSettings(
    int MaxStoryTitleCharacters,
    int MaxStoryLabelCharacters,
    int MaxStoryPromptCharacters,
    int MaxPlayerQuestionCharacters,
    int MaxValidationInstructionCharacters,
    int MaxPlayerAnswerCharacters,
    int MaxPlayerActionCharacters,
    int MaxNarrationCharacters,
    int MaxSuggestedActions,
    int MaxSuggestedActionCharacters,
    int MaxStoryBibleCategoryCharacters,
    int MaxStoryBibleNameCharacters,
    int MaxStoryBibleUpdatesPerResponse,
    int MaxResponseBodyBytes);

public sealed record ConnectionCapabilities(
    bool SupportsModelDiscovery,
    StructuredOutputTier StructuredOutputTier,
    string? TestedModelId,
    DateTimeOffset? TestedAtUtc)
{
    public OutputTokenParameter OutputTokenParameter { get; init; } = OutputTokenParameter.MaxCompletionTokens;
    public InstructionMessageRole InstructionMessageRole { get; init; } = InstructionMessageRole.Developer;
}

public enum OutputTokenParameter { MaxCompletionTokens, MaxTokens }
public enum InstructionMessageRole { Developer, System }

public enum StructuredOutputTier
{
    Untested,
    StrictJsonSchema,
    JsonMode,
    PromptedJson,
    Unsupported
}
```

Null model parameters are not sent to the provider. A provider adapter maps configured parameters to the request fields supported by its API mode.
Do not infer parameter support from model discovery and do not issue test requests for each optional parameter.
Invalidate and retest recorded capabilities when the base URL or selected model changes.
Because the initial version has exactly one API connection, its optional credential uses the fixed application-owned secure-storage key `mellow-narrator.api-credential`. Do not persist that key or any credential value in `ApiConnectionSettings`.

## Configurable Defaults and Bounds

Create the initial configuration from one versioned Core defaults factory. Persist every value below in `api-connection.json`, expose it on the Settings Page and allow each settings section to be reset to its defaults.

| Setting | Default | Allowed range |
|---|---:|---:|
| Request timeout per HTTP attempt | 120 seconds | 10 to 900 seconds |
| Maximum output tokens | 4,096 | 256 to 131,072 |
| Temperature | Unset | 0 to 2 when set |
| Top-p | Unset | 0 to 1 when set |
| Reasoning effort | Unset | Provider-compatible value when set |
| Recent completed turns | 8 | 0 to 100 |
| Maximum Story Bible entries | 200 | 1 to 2,000 |
| Maximum characters per Story Bible entry | 4,000 | 100 to 50,000 |
| Maximum total Story Bible characters | 60,000 | 1,000 to 1,000,000 |
| Story Bible warning threshold | 80% | 50% to 95% |
| Automatic retries after the initial attempt | 2 | 0 to 5 |
| Initial retry delay | 1 second | 0.25 to 30 seconds |
| Maximum retry delay | 10 seconds | 1 to 120 seconds |
| Maximum automatic `Retry-After` wait | 60 seconds | 1 to 600 seconds |

Advanced content limits use these defaults:

| Setting | Default | Allowed range |
|---|---:|---:|
| Story title | 200 | 1 to 1,000 |
| Story State label | 200 | 1 to 1,000 |
| Story Prompt | 20,000 | 100 to 200,000 |
| Player question | 1,000 | 1 to 10,000 |
| Validation instruction | 2,000 | 1 to 20,000 |
| Player answer | 4,000 | 1 to 50,000 |
| Player action | 4,000 | 1 to 50,000 |
| Returned narration | 20,000 | 100 to 200,000 |
| Maximum suggested actions per response | 6 | 1 to 20 |
| Each suggested action | 500 | 1 to 5,000 |
| Story Bible category | 100 | 1 to 1,000 |
| Story Bible entry name | 200 | 1 to 2,000 |
| Story Bible updates per response | 100 | 1 to 1,000 |
| HTTP response body | 2 MiB | 64 KiB to 16 MiB |

The allowed ranges are application safety bounds, not claims about provider capabilities. Do not silently clamp values. Reject an invalid save with field-specific errors, require `MaxDelay >= InitialDelay`, and let the provider return an actionable unsupported-limit error when a locally valid output-token value exceeds its capability.
Base URL and model ID initially have no value and are not supplied by the defaults factory. Both are required before connection testing or any LLM operation; keep the user's other configured settings when either is missing and show a field-specific error.

Use the Story Bible warning percentage independently for entry count, largest-entry characters and total characters; show the approaching-limit warning when any usage reaches the configured percentage.
Content limits apply to newly entered or edited values and to provider responses. Never truncate input or output. Existing durable text which becomes longer than a newly lowered input limit remains readable, but it must be shortened before that value can be edited and saved. Story Bible limit changes use the explicit Increase Limits or Automatically Cull flow.

Apply the response body limit while streaming bytes from HTTP even though generation responses are presented non-streamingly. Abort before buffering more than the configured maximum. Treat schema-level response limit failures as validation failures eligible for the one corrective retry; an oversized HTTP body fails immediately without a corrective retry.

## Versioned Import and Export Models

Use versioned export envelopes which are separate from persistence records and LLM DTOs:

```csharp
public sealed record StoryDefinitionExport(
    int FormatVersion,
    DateTimeOffset ExportedAtUtc,
    StoryDefinition Definition);

public sealed record StoryStateExport(
    int FormatVersion,
    DateTimeOffset ExportedAtUtc,
    StoryState State,
    IReadOnlyList<StoryTurn> Turns);
```

Serialize JSON using camel-case property names, string enum values and ISO 8601 UTC timestamps.
Validate the complete document before import. Migrate supported older format versions and reject unsupported newer versions with a clear error.
Import and copy operations create a deep copy with a new top-level identity, remap nested technical IDs consistently and preserve all internal references.
Imported and copied records are appended to the end of their corresponding ordered list. A copied Story State preserves the source label and all other domain data; only its identifiers and list position differ.
Never include API credentials in an export.

## Persistence and Aggregate Boundaries

Treat `StoryDefinition`, `StoryState` and `WorkspaceState` as separate aggregate boundaries:
- Persist a Story Definition, its questions and its initial Story Bible together.
- Persist a Story State setup, turns, current Story Bible and applied Bible changes together.
- Persist Workspace State separately because it contains temporary UI session data and references durable records.

Persist application data as folders of versioned JSON files under the platform application-data directory. Do not use SQLite or another database.
Store Story Definitions, Story States, turns, Workspace State and non-sensitive settings as plaintext JSON. The initial version does not apply application-level encryption to these files.
Story Definition and Story State exports are also plaintext JSON. API credentials remain excluded from all persistence and export files and are stored only in MAUI Secure Storage.

Use this logical folder layout:

```text
Mellow.Narrator/
  settings/
    api-connection.json
  workspace/
    workspace.json
  story-definitions/
    {storyDefinitionId}.json
  story-states/
    {storyStateId}/
      state.json
      turns/
        00000000-{turnId}.json
        00000001-{turnId}.json
  trash/
    story-definitions/
      {deletedUtc}-{storyDefinitionId}.json
    story-states/
      {deletedUtc}-{storyStateId}/
  staging/
```

Store each Story Definition, including its questions, initial Story Bible and Bible maintenance history, in one JSON file.
Store each Story State in its own folder. `state.json` contains the setup snapshot, label, ordering and timestamps, materialized current Story Bible, Bible maintenance history and the last committed turn sequence. Store each completed turn as an immutable JSON file in the `turns` folder.
Use zero-padded sequence numbers at the start of turn filenames so their filesystem order is their story order. Validate all IDs before using them in paths and derive filenames only from application-owned `Guid` values.
Use `System.Text.Json` with camel-case properties, string enum values, ISO 8601 UTC timestamps and explicit format-version fields.

Write every mutable JSON document to a temporary file in the same directory, flush it and atomically replace the destination. Retain the previous valid document beside it using the `.bak` suffix as the single last-known-good backup.
After replacement, reopen and validate the new primary document but keep the `.bak` file. A later successful replacement overwrites the older backup with the then-current valid primary document.
Immutable turn files do not require individual backups because they are never modified after publication.
Use an in-process asynchronous lock per aggregate so two operations cannot write the same Story Definition, Story State or Workspace State concurrently.
The released GUI is the sole process permitted to open its application-data tree. The unreleased CLI and automated tests always use isolated data roots, so cross-process filesystem locking is outside the initial scope.

Every durable mutation has one explicit atomic commit point. Files written before that point are staged output only. Cancellation, application termination or recovery before the commit point restores the previous validated primary document or aggregate and removes the staged output. Once the commit point succeeds, the new state must already be complete and internally consistent.

Commit a completed story turn as follows:
1. Validate the LLM response and calculate the complete `StoryTurn` and resulting `StoryState` in memory.
2. Serialize and flush both documents to temporary files.
3. Atomically publish the immutable turn file as staged, uncommitted output.
4. Atomically replace `state.json` with the resulting materialized state and new last-committed sequence. This replacement is the commit point.

The last committed sequence in a valid `state.json` is authoritative. On startup, treat any turn file after that sequence as orphaned output from an interrupted operation and remove it instead of replaying it. If `state.json` refers to a missing or invalid turn, restore the last internally consistent `state.json.bak` and remove turn files after its committed sequence. If neither primary nor backup describes a consistent aggregate, stop loading that Story State and report a recovery error rather than guessing or rolling forward.
For any other missing or invalid mutable JSON document, automatically restore its valid `.bak` file and report that recovery occurred. If both primary and backup are invalid, quarantine them and surface a recovery error.
Create new and copied Story State folders completely under `staging`, validate every generated file, then move the completed folder into `story-states` as their commit point. Ignore and safely clean incomplete staging folders during startup recovery so an interrupted creation leaves no durable Story State.
Commit a confirmed standalone Bible cull by atomically replacing the selected Story Definition document or Story State `state.json` with both the reduced Bible and its maintenance record. That replacement is the cull's commit point; interruption before it restores the unchanged aggregate.

Move deleted Story Definition files and complete Story State folders into their matching `trash` subfolder instead of deleting them immediately. Include the UTC deletion time in a filename-safe `yyyyMMddTHHmmssfffZ` form in the trash item name and move any retained backups with the item.
Keep at most 10 deleted aggregates and at most 100 MB of trash data in total. After a successful move to trash, permanently purge the oldest items until both limits are satisfied. Always retain the newest item even when that single item exceeds the size limit.
Allow a trashed Story Definition or Story State to be restored. Restore its original identity when that identity is unused; otherwise perform the normal deep-copy ID remapping and restore it as a new record. Append restored records to the end of their ordered list.
Allow permanent deletion of an individual trash item and allow the entire trash folder to be emptied, both with confirmation.

List Story Definitions by reading the definition documents. List Story States by reading only each `state.json`; load the configured number of recent immutable turn files when a Play Story Page opens and load older turns on demand for display.
Persist Workspace State as an atomically replaced `workspace.json`. Persist non-sensitive API configuration in `api-connection.json`. API credentials remain exclusively in MAUI Secure Storage.
Apply persistence-format migrations to in-memory documents first, validate the migrated result and then replace the original file atomically while retaining the previous valid version as its backup. Quarantine an unreadable document and its unreadable backup and surface a recovery error instead of silently overwriting them.
Build import and export documents through the versioned DTOs; do not treat internal persistence files as export files.
Do not persist LLM response DTOs before validation or expose persistence records directly to the UI.

## Persistence Interfaces and Dependency Injection

Define the persistence contracts in `Mellow.Narrator.Core`:
- `IStoryDefinitionRepository` for listing, loading, saving and moving Story Definitions to trash.
- `IStoryStateRepository` for listing, loading recent turns on demand, capturing a complete aggregate snapshot for export/copy, creating, committing turns, applying granular label/order metadata updates, copying and moving Story States to trash. Whole-state saves must reject a stale last-committed sequence rather than overwrite a newer commit.
- `IWorkspaceStateStore` for loading and atomically saving open-tab state.
- `IApiConnectionSettingsStore` for non-sensitive connection settings.
- `ITrashStore` for listing, restoring, permanently deleting and purging trash items.

Repository APIs use domain models, asynchronous methods and `CancellationToken`. They must not expose JSON persistence document types or filesystem paths to Core application services.
`IStoryStateRepository` exposes one atomic commit operation which receives the validated turn and resulting Story State together. Core must not attempt to coordinate individual file writes.

Implement these interfaces in `Mellow.Narrator.Persistence` using the JSON folder, backup, recovery, migration, staging and trash rules in this plan.
Mellow.Narrator.Persistence must not reference MAUI. Accept an absolute, validated application-data root through `PersistenceOptions` so GUI, CLI and tests can select the storage location.
Provide an `AddMellowNarratorPersistence(IServiceCollection, PersistenceOptions)` registration extension. Register one shared instance of each JSON repository per application process so per-aggregate locks and recovery state are shared.

Register Core application services separately through `AddMellowNarratorCore(IServiceCollection)`. All Core use cases receive repository interfaces through constructor injection; do not use a service locator or static repository access.

The composition roots are:
- Mellow.Narrator.Gui registers Core, OpenAiCompatible, passes `FileSystem.AppDataDirectory` to Persistence and registers `MauiSecureStorageService`.
- Mellow.Narrator.Cli registers Core, OpenAiCompatible and Persistence with an explicitly selected isolated test directory and supplies credentials only for manual provider testing.
- Tests register Core with fakes for unit tests or Persistence with a fresh temporary root for integration tests.

## Secure Storage Interface and Dependency Injection

Define the secure-storage abstraction in `Mellow.Narrator.Core.Security` so Core application services can use credentials without referencing MAUI:

```csharp
public static class SecureStorageKeys
{
    public const string ApiCredential = "mellow-narrator.api-credential";
}

public interface ISecureStorageService
{
    Task<string?> GetAsync(
        string key,
        CancellationToken cancellationToken = default);

    Task SetAsync(
        string key,
        string value,
        CancellationToken cancellationToken = default);

    Task<bool> RemoveAsync(
        string key,
        CancellationToken cancellationToken = default);
}
```

The interface deliberately omits enumeration and `RemoveAll`: Core should access only credential keys it owns and should not be able to inspect or erase unrelated secure values. Implementations reject blank keys, and `SetAsync` rejects null values. A missing key returns `null`; storage failures are reported distinctly from a missing value.

Implement `MauiSecureStorageService` in `Mellow.Narrator.Gui.Services.Security`. It wraps `Microsoft.Maui.Storage.ISecureStorage`, supplied through its constructor, and adapts `GetAsync`, `SetAsync` and `Remove`. Check cancellation before and after calls where the platform API does not accept a `CancellationToken`.

Register the platform service and adapter in the GUI composition root:

```csharp
builder.Services.AddSingleton(
    Microsoft.Maui.Storage.SecureStorage.Default);
builder.Services.AddSingleton<ISecureStorageService, MauiSecureStorageService>();
```

Core API-connection services receive `ISecureStorageService` through constructor injection. They use `SecureStorageKeys.ApiCredential` to load the optional credential immediately before making an authenticated provider request and to store or remove it when Settings is saved. Core must never call MAUI static APIs or use a service locator.

Credentials are not file persistence: `Mellow.Narrator.Persistence` neither references nor implements `ISecureStorageService`, and no credential or secure-storage key is written to JSON. If secure storage is unavailable or fails, show an actionable credential-storage error and do not fall back to JSON, preferences, logs or another plaintext store. Never include a credential value in exceptions, diagnostics or telemetry.

Serialize Settings saves with the start of new provider requests. Before changing a credential, read and retain its previous value in memory, write or remove the secure value, then atomically save the non-sensitive settings. Do not publish the new in-memory configuration until both operations succeed. If the settings save fails, make a best-effort restoration of the previous secure value and report the failure. If restoration also fails, mark the connection as requiring the credential to be entered again and do not send an authenticated request until it is saved successfully. Never persist the retained value during this recovery.

The CLI may register a process-local/manual implementation only for explicitly invoked provider smoke tests. Unit tests register an in-memory fake; neither is a production persistence fallback.

The project reference direction is:
- Mellow.Narrator.OpenAiCompatible -> Mellow.Narrator.Core
- Mellow.Narrator.Persistence -> Mellow.Narrator.Core
- Mellow.Narrator.Gui -> Mellow.Narrator.Core, Mellow.Narrator.OpenAiCompatible and Mellow.Narrator.Persistence
- Mellow.Narrator.Cli -> Mellow.Narrator.Core, Mellow.Narrator.OpenAiCompatible and Mellow.Narrator.Persistence
- Mellow.Narrator.Core -> no GUI, OpenAiCompatible or Persistence project

# Testing Strategy

## General Approach

Keep most behavior in `Mellow.Narrator.Core` and test it without starting MAUI.
Make time, ID generation, LLM calls and persistence boundaries controllable in tests. Use a fake `TimeProvider`, deterministic ID generation and fake provider responses so tests are repeatable.
Test JSON repositories against real isolated temporary folders rather than mocking filesystem behavior. Tests must never read or write the user's real application-data directory or Secure Storage.
Add a composition test which builds the dependency-injection container and resolves every Core use case with the JSON Persistence implementations.
Add a GUI composition test which resolves `ISecureStorageService` to `MauiSecureStorageService`.
Add a composition test which resolves the Core provider abstraction to the OpenAiCompatible adapter with its typed `HttpClient`.
Normal automated tests must not require network access, a live provider or an API key. Provide optional manual provider smoke tests through the CLI, enabled only by explicit configuration.

## Domain Unit Tests

Cover the following domain behavior with fast unit tests:
- Story Definition creation, editing and overwrite behavior.
- Guarded Story Definition deletion and preservation of existing Story States.
- Stable IDs, ordering and UTC timestamp handling.
- Versioned default-settings creation, every inclusive settings boundary and every invalid cross-field combination.
- Rejection rather than clamping of out-of-range configuration.
- Story Definition snapshots and independent initial-Bible copies.
- Sequential player questions, preceding-answer context and non-blocking warning acceptance.
- Story State and opening-turn creation.
- Turn sequence enforcement and `LastActionAtUtc` updates.
- Story Bible add, replace and remove operations.
- Importance validation and last-relevant turn updates.
- Automatic relevance for added and replaced entries.
- Rejection of unknown IDs, duplicate updates, unknown relevance IDs, malformed entries and size-limit violations.
- Deterministic culling by lowest importance, oldest relevance and entry-ID tie-breaker.
- Removal of individually oversized entries before count and total-size culling.
- Exact automatic-cull previews and confirmed atomic maintenance of only the selected aggregate.
- Cancellation of a limit warning without mutation and successful rechecking after limits are increased.
- Story Bible maintenance history, limit snapshots, timestamps and unchanged Story State `LastActionAtUtc`.
- Applied Bible change before-and-after values.
- Distinction between LLM-requested changes and automatic culls.
- Atomic calculation of a proposed turn without mutating the previous Story State.
- Complete Story State copying, technical-ID remapping and reference preservation.
- Tab ordering, fixed-tab positions, duplicate Play Story prevention and one editor per Story Definition.

Every critical domain invariant must have positive, boundary and failure tests.

## Request and Context Tests

Verify prompt and request construction independently of HTTP:
- The currently selected model is used for every new request.
- The Story Prompt, final player responses and complete Story Bible are always included.
- Every sent Bible entry includes its importance and last-relevant turn number.
- Exactly the configured maximum number of recent completed turns is included when available.
- Older turns and previous suggested actions are omitted.
- Narration is never summarized and the Story Bible is never truncated.
- Story Bible warning and hard-size limits behave correctly.
- A nonconforming current Bible blocks an LLM request until its limits are increased or its automatic cull is confirmed.
- Opening-scene and story-turn responses flag relevant existing Bible entries.
- Later player-answer warning checks include all preceding chosen answers and no later unanswered questions.
- Failed player-answer validation requests retain the answer as `NotValidated`, never advance and never expose a continue-unchecked path.
- A manual retry rebuilds the request using the currently selected settings.
- Configured input and structured-response limits are enforced without truncation.

Use captured request fixtures to detect unintended prompt or schema changes. Review fixture changes rather than updating them automatically.

## Structured Response Tests

Test every LLM DTO and conversion path using stored JSON fixtures:
- Valid strict-schema, JSON-mode and prompted-JSON responses.
- Refusals, empty responses, invalid JSON and schema violations.
- Missing fields, unexpected fields, invalid enum values and oversized values.
- Missing, duplicate, removed or unknown relevant-entry IDs and invalid importance values.
- One corrective retry with validation feedback.
- Failure without state mutation after the corrective retry is exhausted.
- Story Definition initial-Bible generation.
- Player-answer warning responses.
- Opening-scene and normal-turn responses.
- Proposed-to-applied Story Bible change conversion.

## Provider Adapter Contract Tests

Use a fake HTTP handler or local in-process test server to verify:
- Base URL and endpoint construction.
- Optional Bearer authentication without credential logging.
- Optional model discovery and manual model-ID fallback.
- Strict JSON Schema, JSON mode and prompted-JSON capability probing.
- Negotiation and reuse of modern `max_completion_tokens`/developer and legacy `max_tokens`/system request contracts.
- Non-streaming Chat Completions request and response handling.
- Cancellation and one in-flight request per Story State.
- Tab closure, Android backgrounding and process recreation during an in-flight request.
- Persistence and restoration of the input required for a manual retry.
- Configurable retry counts, exponential delays, maximum delay and jitter bounds for rate limits and server failures.
- `Retry-After` handling below, at and above the configured automatic-wait maximum.
- Per-attempt timeout, cancellation during backoff and fresh transport-retry allowance for a corrective request.
- Incremental enforcement of the configured HTTP response-body limit.
- No automatic retry for authentication, invalid-model or unsupported-parameter errors.
- No silent model fallback.
- Clear mapping from provider failures to user-facing error categories.

## Secure Storage Tests

Use an in-memory `ISecureStorageService` fake in Core tests to verify credential save, replacement, retrieval, removal and missing-key behavior.
Test `MauiSecureStorageService` against a mocked MAUI `ISecureStorage` abstraction, covering set/get/remove delegation, blank arguments, cancellation and platform failures.
Verify that API credentials are always accessed through `SecureStorageKeys.ApiCredential`, and that settings JSON contains neither the key nor the credential.
Inject failures into each step of Settings saves to verify rollback of the previous secure value, withholding of the new in-memory configuration and the re-entry-required state when rollback fails.
Verify that unavailable secure storage produces an actionable error and never creates a plaintext fallback.
Verify that credentials are absent from JSON persistence, exports, captured HTTP diagnostics, logs, exception messages and telemetry.

## JSON Persistence and Recovery Tests

Run persistence tests in a newly created temporary application-data tree and verify:
- Story Definition, all configurable settings and workspace round trips.
- Story State creation with an opening turn.
- Immutable sequential turn filenames and materialized `state.json`.
- Atomic replacement of mutable JSON documents.
- Retention and rotation of the single last-known-good `.bak` document.
- Automatic restoration from a valid backup when a primary document is missing or invalid.
- Rollback and removal of an orphan turn file published before `state.json` reached its commit point.
- Validation of relevant-entry updates and automatic culls stored in each committed immutable turn file.
- Detection of `state.json` which references a missing or invalid turn.
- Recovery or quarantine of malformed JSON and interrupted temporary files.
- Cleanup of incomplete staging folders.
- Move-to-trash, restoration, conflicting-ID remapping and bounded oldest-first trash purging.
- Concurrent-write serialization by aggregate locks.
- Complete Story State copy publication.
- Loading summaries without loading full turn history.
- Loading recent turns and older turns on demand.

Inject cancellation and process-failure simulations before every commit point to prove startup recovery restores the previous valid state and removes staged output. Inject failures immediately after each commit point to prove the new state is already complete and internally consistent, never a partial combination.

## Import, Export and Migration Tests

Maintain versioned JSON fixtures for every supported import and persistence format.
Test:
- Story Definition and Story State export/import round trips.
- Complete preservation of Story State domain data.
- Top-level and nested ID remapping with reference preservation.
- Duplicate import of the same file.
- Supported older-version migration.
- Clear rejection of unsupported newer versions.
- Invalid, truncated and oversized documents.
- Path-traversal and untrusted-filename attempts.
- Confirmation that credentials are never exported.

## Use-Case Integration Tests

Exercise complete Core workflows using a fake provider and temporary JSON repositories:
1. Create a Story Prompt draft and generate a Story Definition.
2. Reopen and edit the Story Definition.
3. Answer questions sequentially, accept a warning and create a Story State.
4. Play several turns and restart from persisted files.
5. Fail and retry a turn without partial state changes.
6. Copy the Story State and advance the original and copy independently.
7. Export, import, reopen, move records to trash and restore them.

## UI and Platform Tests

Keep UI automation focused on critical integration behavior:
- Application startup and restoration of open tabs.
- Fixed Settings, Story Definition List and Play Story List tabs.
- Settings defaults, allowed-range help, validation without clamping, persistence and Reset Section to Defaults behavior.
- Standard Manage Tabs modal and Move Earlier/Move Later actions on Windows and Android.
- Restoration of the selected tab and logical order through the platform-standard tab presentation.
- Confirmation that the GUI registers no custom renderers, handlers or third-party UI control packages.
- One Story Prompt editor per existing Story Definition.
- Story Definition deletion with open read-only, editor and Start Story tabs.
- One Play Story Page per Story State and activation of an existing tab.
- Sequential question warnings and Continue With Current Answer.
- Player-answer API failure with blocking Retry and Cancel behavior and no Continue Without Validation action.
- Failed player-action Retry and Cancel behavior.
- Confirmation before discarding Start Story progress or a pending Play Story action.
- Story State copy creation and independent opening.
- Read-only initial and current Story Bible inspection, search and filtering.
- Lazy, newest-first Story Bible change history combining turn changes and maintenance records.
- Confirmation that inspection cannot edit entries or alter their metadata.
- Secure credential entry without displaying or logging the stored value.
- Trash listing, restoration, permanent deletion and Empty Trash confirmation.

Run the same applicable scenarios on Windows and an Android emulator. Test view models and commands in the fast test project; reserve device automation for behavior which depends on MAUI rendering, navigation, lifecycle or platform services.

## Non-Functional Tests

Include representative tests for:
- Long stories with many immutable turn files.
- Story Bibles near every configured size limit.
- Existing Bibles made nonconforming by lower settings, including Increase Limits, confirmed Automatically Cull and Cancel paths.
- Large numbers of Story Definitions, Story States and open tabs.
- Startup and list-loading performance without eager history loading.
- Android pause, resume and process recreation.
- Cancellation during provider requests.
- Redaction of credentials and full prompts from logs and exceptions.
- Safe handling of malformed and excessively large imported JSON.

## Continuous Integration and Release Gates

For every change, build the solution and run Core unit, schema, provider-contract, persistence and use-case integration tests.
Build both Windows and Android targets in continuous integration.
Run the fast test suite on every change. Run Windows and Android device UI smoke tests before a release and whenever navigation, tab behavior or platform integration changes.
Report code coverage, but do not use a percentage as a substitute for invariant and failure-path tests.
A release is blocked by any failed automated test, unsupported persistence migration, failed import/export compatibility fixture or failed critical UI smoke test.

# User Interface

This will be tab-based with 7 different types of tab.
The open tabs and their order will be persisted so they can be restored after the application is closed and reopened.

## Standard MAUI Layout

Use a standard .NET MAUI `TabbedPage` as the application root. Each tab is a standard `NavigationPage` containing a `ContentPage`. Add and remove dynamic tabs through the `TabbedPage.Children` collection and activate a tab through `CurrentPage`.

Accept the native tab placement and overflow behavior supplied by MAUI on Windows and Android. Do not provide vertical-tab orientation, draggable tabs or a custom tab strip.

Build pages from standard MAUI controls such as `Grid`, `ScrollView`, `CollectionView`, `Label`, `Entry`, `Editor`, `Picker`, `Button`, `ToolbarItem`, `ActivityIndicator` and standard modal navigation and alerts. Use `VisualStateManager` and ordinary layout breakpoints for responsive layouts.

Do not use Shell, third-party control libraries, custom renderers, custom handlers or platform-specific tab implementations. Reusable page sections may be ordinary MAUI `ContentView` classes composed solely from standard controls. Implement collapsible sections with a standard Button and an `IsVisible` content container rather than a non-standard expander control.

Provide Close and Manage Tabs as normal `ToolbarItem` commands. Use standard modal `ContentPage` screens for multi-step or management flows rather than custom popups.

## Tab Reordering

The Settings, Story Definition List and Play Story List tabs are locked in the first three positions and cannot be reordered.
Maintain one logical order for all unlocked tabs after the three fixed tabs.
The Manage Tabs toolbar command opens a standard modal `ContentPage` containing a `CollectionView` of unlocked tabs. Each row has standard Move Earlier and Move Later buttons; disable the button which would move the row beyond the unlocked region.
After a move, reorder the corresponding `TabbedPage.Children` using standard collection operations while preserving the currently selected page. Persist each successful move immediately so it survives application restart.
Use this same button-based mechanism on Windows and Android. Do not implement drag, drop, swipe or long-press reordering.

## Settings Page

Always exactly 1 tab. This is locked as the 1st (top/leftmost) tab.
Allow the user to configure a connection to an OpenAI-compatible API.
Store API keys and other credentials using `Microsoft.Maui.Storage.SecureStorage`. Credentials must not be stored in ordinary configuration files, written to logs or included in exported Story Definition or story state JSON files.
Persist non-sensitive connection settings, such as the API endpoint and selected model, separately from credentials.
With an API connected, attempt to load a list of available models and allow the user to select one. If model discovery is unavailable, allow the user to enter a model ID manually.
Explain that changing the selected model applies to all subsequent LLM operations, including existing stories.
Show request timeout, maximum output tokens, optional temperature, optional top-p, optional reasoning effort, recent-turn count and maximum Story Bible entry count as normal generation settings. Send optional model parameters only when configured.
Provide an Advanced section for retry timing, Story Bible warning/per-entry/total limits and all input, structured-response and response-body limits.
Show each setting's allowed range and default value. Validate fields before saving, preserve the user's invalid text while showing errors, and provide Reset Section to Defaults actions which still require Save before taking effect.
Before saving lower Story Bible limits, report how many existing Story Definitions and Story States would no longer conform. Saving the settings is allowed after acknowledgement but does not modify those records.
Allow the user to test the connection and show its detected capabilities.
Provide a Manage Trash view showing deleted Story Definitions and Story States, their deletion times and total trash usage. Allow restore, confirmed permanent deletion and confirmed Empty Trash operations.
Persist the configuration.

## Story Definition List Page

Always exactly 1 tab. This is locked as the 2nd tab.
A list of Story Definitions.
Allow the user to create a new Story Definition by opening a new blank Story Prompt Page.
Allow the user to re-order Story Definitions using standard Move Earlier and Move Later buttons in the list.
Allow the user to select a Story Definition.
Allow the user to view the selected Story Definition in a new tab (Story Definition Page).
Allow the user to edit the selected Story Definition's Prompt in a Story Prompt Page. If that Story Definition is already being edited, activate its existing Story Prompt Page instead of opening another one.
Allow the user to delete the selected Story Definition using the guarded Story Definition deletion flow.
Allow the user to start a new story using the selected Story Definition (Start Story Page).
Allow the user to export a copy of the selected Story Definition as a JSON file.
Allow the user to import a JSON file containing a Story Definition to add it to the list.

## Play Story List Page

Always exactly 1 tab. This is locked as the 3rd tab.
A list of all durable Story States.
Display each Story State's label, the time it was started and the time of its last completed player action.
Allow the user to re-order Story States using standard Move Earlier and Move Later buttons in the list.
Allow the user to set or change a Story State's label.
Indicate which Story States are already open in a Play Story Page.
Allow the user to select and reopen a Story State in a Play Story Page.
Only one Play Story Page may be open for a given Story State. If the Story State is already open, activate its existing tab instead of opening another one.
Allow the user to create a complete copy of the selected Story State. Duplicate all of its domain data, assign new technical identifiers, persist it as a new durable Story State and open it in a new Play Story Page, even when the original Story State is already open.
Allow the user to move the selected Story State to trash after confirmation. A Story State which is open in a Play Story Page must be closed before it can be moved to trash.
Allow the user to export a copy of the selected Story State as a JSON file.
Allow the user to import a story state JSON file as a new durable Story State and optionally open it in a Play Story Page.

## Story Definition Pages

Multiple copies of this page may be open symultaneously as tabs.
A read-only view of the Title, Story Prompt, player questions and initial Story Bible. Use the Story Bible inspector to show entry metadata and the definition's maintenance history.
If the initial Story Bible exceeds current limits, show the explicit Increase Limits, Automatically Cull and Cancel flow before it can be used to start a story.
Allow the user to edit the Story Definition's Prompt. If it is already being edited, activate the existing Story Prompt Page; otherwise replace this tab with a new Story Prompt Page.
Allow the user to start a new story using this Story Definition (replace this tab with a Start Story Page).

## Story Prompt Pages

Multiple copies of this page may be open symultaneously as tabs, but only one may edit a given existing Story Definition. Multiple independent drafts for new Story Definitions may be open.
Allow the user to enter a title for the story.
Allow the user to enter the Story Prompt which will be used to generate the initial Story Bible.
Allow the user to enter a list of questions which the player must answer before playing the story, along with natural-language validation rules that the configured LLM will use to produce non-blocking warnings. For example "What is your name?" with the validation "Should not be a girls' name." or "How old are you?" with the validation "Must be at least 18 years old."
Persist the details entered only as part of the open tab state so the Story Prompt Page can be restored after the application is closed and reopened. Story Prompt Page drafts are not durable records; Story Definitions are the durable records.
When the user closes a non-empty or modified Story Prompt Page, ask for confirmation before deleting its temporary draft. Closing a draft created to edit an existing Story Definition must not alter the existing Story Definition.
Provide a button which will allow the user to generate a populated Story Definition, including its initial Story Bible. If this tab was opened to edit an existing Story Definition then give the option to overwrite it or create a new one. Replace the tab with a Story Definition page.

## Start Story Pages

Multiple copies of this page may be open symultaneously as tabs.
When opening a Start Story Page, resolve any limit violation in the source Story Definition before taking its temporary setup snapshot.
Ask the player questions one at a time in their defined order. Use the configured LLM to check each answer against its natural-language validation rule, including all preceding chosen answers in later warning-check requests.
Treat an answer which does not satisfy its validation rule as a warning only. If a warning is returned, allow the player to change and recheck the answer or continue with the current answer.
If the validation request itself fails, keep the answer on the current question and show Retry or Cancel. Do not allow the player to continue to the next question until a successful validation response returns either no warning or a warning the player explicitly accepts.
Once every answer is valid or has been explicitly accepted with a warning, copy the Story Definition's initial Story Bible and assign new IDs to its entries. Ask the LLM for the opening narration, suggested actions, relevant existing Bible entries and any Story Bible updates arising from the player's answers.
If Settings changes make an already-open Start Story snapshot nonconforming, resolve it before sending the opening-scene request. Increase Limits leaves the snapshot unchanged; Automatically Cull changes only the temporary snapshot and carries its maintenance record into the Story State when creation succeeds; Cancel preserves the draft without sending a request.
Validate and apply the relevance flags and updates, automatically cull to the configured entry count, persist the new durable Story State and its opening turn atomically, then replace this tab with a Play Story Page for that Story State.
When a Story State is created, give it a default label based on its Story Definition title and record the time it was started.

## Play Story Pages

Multiple copies of this page may be open symultaneously as tabs, but each must be associated with a different Story State.
Use the Story State's label as the Play Story Page's tab label.
Displays the story narration based on recent narration, player action and the current Story Bible.
Provide collapsed-by-default panels for inspecting the complete current Story Bible and its combined turn and maintenance change history.
Provides suggested actions for the player to choose from.
Provides a text box for the player to enter any action they want (if they don't like the suggestions).
Before sending a player action, resolve any current Story Bible limit violation through Increase Limits, confirmed Automatically Cull or Cancel.
If a player-action request fails, retain the entered action and provide Retry and Cancel actions. Allow the player to edit the action before retrying.
After the player selects or enters an action, request the next scene, suggested actions, relevant existing Story Bible entries and a list of Story Bible updates. Validate and apply the relevance flags and updates, automatically cull to the configured entry count, append the narration and record the time of the completed player action.
Persist each completed change to the Story State, including recent narration, player actions, suggested actions, relevant Bible entry IDs, all applied Story Bible changes including automatic culls and the resulting Story Bible. The Story State is a durable record which exists independently of its tab.
Persist Story State timestamps in UTC and display them in the user's local time. Until the first player action is completed, show that there is no last-action time.
Closing a Play Story Page closes only the tab and does not delete its Story State. The Story State can be reopened from the Play Story List Page.
Provide a button to create and open a copy of the current Story State as a new independent branch. Disable it while an LLM request is in flight.
Provide a button to export the current state of the story as a JSON file.
