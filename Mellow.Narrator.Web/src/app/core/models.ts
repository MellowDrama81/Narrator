export interface StoryBibleEntry {
  id: string;
  category: string;
  name: string;
  knownFacts: string[];
  secretFacts: string[];
  importance: number;
  lastRelevantTurnNumber: number;
}

export interface StoryBibleUpdate {
  operation: 'add' | 'replace' | 'remove';
  entryId: string | null;
  entry: Omit<StoryBibleEntry, 'id' | 'lastRelevantTurnNumber'> | null;
}

// A Planned Event is a future plot point the narrator is steered toward, secret from the player.
// Importance and urgency are independent: importance 5 (the maximum) marks the event mandatory -
// applyPlannedEvents refuses to remove it except with outcome 'fulfilled', and never culls it, so the
// narrator is forced to work it into the story rather than letting it quietly drop. Urgency (1-5)
// instead says how directly and soon to steer toward it. condition is an optional freeform description
// of what must happen, or what state the story must be in, before this event can be pursued - narrative
// prose the narrator interprets each turn, not a structured reference to another entry. Null or empty
// means the event has no prerequisite and is pursuable immediately according to its own importance and
// urgency.
export interface PlannedEvent {
  id: string;
  description: string;
  importance: number;
  urgency: number;
  condition: string | null;
  lastRelevantTurnNumber: number;
}

export type PlannedEventOutcome = 'fulfilled' | 'abandoned';

// Wire-format proposal for a Planned Event - see the PlannedEvent doc comment for what condition means.
export interface ProposedPlannedEvent {
  description: string;
  importance: number;
  urgency: number;
  condition: string | null;
}

export interface PlannedEventUpdate {
  operation: 'add' | 'replace' | 'remove';
  entryId: string | null;
  entry: ProposedPlannedEvent | null;
  outcome: PlannedEventOutcome | null;
}

// A Story Condition is a fixed victory or loss condition defined on the Story Definition and copied
// verbatim (with a remapped id, same as Story Bible/Planned Events) into every Story State started from
// it. Unlike Planned Events, the set never grows or shrinks during play - the narrator only ever reports
// a condition as revealed and/or met, never adds, replaces, or removes one - so no maintenance/cull
// machinery exists for it. Secret controls whether the narrator may ever state the condition's content
// directly in narration: a secret condition must stay implied only through the ordinary events that
// satisfy it, exactly like a Planned Event, while a non-secret one should be woven into the prose once
// something in the story makes it relevant (never as an upfront list) and is then tracked as "revealed".
// Both secret and non-secret conditions are tracked as "met" once actually satisfied; a condition, once
// met, stays met for the rest of the story even though the player may choose to keep playing past it.
export interface StoryCondition {
  id: string;
  description: string;
  secret: boolean;
}

export interface ProposedStoryCondition {
  description: string;
  secret: boolean;
}

export interface StoryDefinition {
  id: string;
  title: string;
  storyPrompt: string;
  initialEventsPrompt: string;
  initialStoryBible: StoryBibleEntry[];
  initialPlannedEvents: PlannedEvent[];
  initialVictoryConditions: StoryCondition[];
  initialLossConditions: StoryCondition[];
  sortOrder: number;
  createdAtUtc: string;
  updatedAtUtc: string;
}

export interface StoryTurn {
  id: string;
  storyStateId: string;
  sequenceNumber: number;
  playerAction: string | null;
  narration: string;
  suggestedActions: string[];
  relevantStoryBibleEntryIds: string[];
  storyBibleUpdates: StoryBibleUpdate[];
  relevantPlannedEventIds: string[];
  plannedEventUpdates: PlannedEventUpdate[];
  // Newly revealed/met this turn only - not cumulative (see StoryState for the running totals). A
  // condition already revealed or met in an earlier turn is never repeated here.
  revealedVictoryConditionIds: string[];
  metVictoryConditionIds: string[];
  revealedLossConditionIds: string[];
  metLossConditionIds: string[];
  completedAtUtc: string;
  modelId: string;
}

export interface StoryState {
  id: string;
  label: string;
  sourceStoryDefinitionId: string | null;
  definition: Pick<StoryDefinition,
    'title' | 'storyPrompt' | 'initialEventsPrompt' | 'initialStoryBible' | 'initialPlannedEvents' |
    'initialVictoryConditions' | 'initialLossConditions'>;
  currentStoryBible: StoryBibleEntry[];
  currentPlannedEvents: PlannedEvent[];
  currentVictoryConditions: StoryCondition[];
  currentLossConditions: StoryCondition[];
  // Running totals across every turn so far - see StoryTurn for this-turn-only deltas.
  revealedVictoryConditionIds: string[];
  metVictoryConditionIds: string[];
  revealedLossConditionIds: string[];
  metLossConditionIds: string[];
  // A compact, narrator-maintained prose recap of everything about the story so far that doesn't fit
  // the Story Bible's atomic facts - the only memory of anything that has scrolled out of the raw
  // recent-turn history sent with each request. Empty until the opening turn establishes it; the
  // narrator rewrites (not appends to) this every turn, so it stays roughly constant in length rather
  // than growing without bound. See TurnGeneration.storySummary.
  storySummary: string;
  sortOrder: number;
  startedAtUtc: string;
  lastActionAtUtc: string | null;
  turns: StoryTurn[];
}

// Mirrors Mellow.Narrator.Core's ConnectionCapabilities.StructuredOutputTier. Tracks what response
// shape the provider has actually been proven to support, learned by probing during real requests.
export type StructuredOutputTier = 'untested' | 'strictJsonSchema' | 'jsonMode' | 'promptedJson' | 'unsupported';
// Mirrors ConnectionCapabilities.OutputTokenParameter - which request field this provider accepts for
// capping output length.
export type OutputTokenParameter = 'maxCompletionTokens' | 'maxTokens';
// Mirrors ConnectionCapabilities.InstructionMessageRole - which message role this provider accepts for
// system-level instructions.
export type InstructionMessageRole = 'system' | 'developer';
export type TurnPipelineMode = 'oneCall' | 'twoCalls' | 'threeCalls' | 'fourCalls' | 'fiveCalls' | 'sevenCalls' | 'sevenCallsParallel' | 'eightCalls';
export type GenerationCall = 'storyDefinition' | 'turn' | 'adjudication' | 'scenePlan' | 'planCritic' | 'narration' | 'storyBibleAnalysis' | 'plannedEventAnalysis' | 'conditionSummaryAnalysis' | 'stateExtraction' | 'proseRevision';
export interface ModelCapability {
  structuredOutputTier: StructuredOutputTier;
  outputTokenParameter: OutputTokenParameter;
  instructionMessageRole: InstructionMessageRole;
  testedAtUtc: string;
}
export interface ApiConnectionProfile { id: string; name: string; baseUrl: string; apiKey: string; modelCapabilities?: Record<string, ModelCapability>; }
export interface GenerationCallRoute {
  connectionId: string;
  modelId: string;
  requestTimeoutSeconds?: number;
  maxOutputTokens?: number | null;
  temperature?: number | null;
  topP?: number | null;
  reasoningEffort?: string;
  maxAutomaticRetries?: number;
  retryInitialDelaySeconds?: number;
  retryMaxDelaySeconds?: number;
  retryMaxRetryAfterSeconds?: number;
}

export interface AppSettings {
  key: 'app';
  baseUrl: string;
  modelId: string;
  apiKey: string;
  requestTimeoutSeconds: number;
  maxOutputTokens: number | null;
  temperature: number | null;
  topP: number | null;
  reasoningEffort: string;
  recentTurnCount: number;
  maxStoryBibleEntries: number;
  maxStoryBibleEntryCharacters: number;
  maxStoryBibleCharacters: number;
  storyBibleWarningPercent: number;
  maxPlannedEvents: number;
  maxPlannedEventCharacters: number;
  maxPlannedEventsCharacters: number;
  plannedEventsWarningPercent: number;
  minSuggestedActions: number;
  maxSuggestedActions: number;
  minParagraphs: number;
  maxParagraphs: number;
  maxStoryTitleCharacters: number;
  maxStoryLabelCharacters: number;
  maxStoryPromptCharacters: number;
  maxPlayerActionCharacters: number;
  maxNarrationCharacters: number;
  maxSuggestedActionCharacters: number;
  maxStoryBibleCategoryCharacters: number;
  maxStoryBibleNameCharacters: number;
  maxStoryBibleUpdatesPerResponse: number;
  maxPlannedEventDescriptionCharacters: number;
  maxPlannedEventConditionCharacters: number;
  maxPlannedEventUpdatesPerResponse: number;
  maxConditions: number;
  maxConditionDescriptionCharacters: number;
  maxStorySummaryCharacters: number;
  minSentencesPerParagraph: number;
  maxSentencesPerParagraph: number;
  maxAutomaticRetries: number;
  retryInitialDelaySeconds: number;
  retryMaxDelaySeconds: number;
  retryMaxRetryAfterSeconds: number;
  maxResponseBodyBytes: number;
  // Experimental: split each story turn into focused calls that can be compared with one another.
  turnPipeline: TurnPipelineMode;
  connections: ApiConnectionProfile[];
  generationCallRoutes: Partial<Record<GenerationCall, GenerationCallRoute>>;
  // Connection-capability state, learned by probing the provider (see ConnectionCapabilities in
  // Settings.cs). Not user-editable - a later agent negotiates/probes and persists these
  // automatically. Whenever baseUrl or modelId changes, these get reset back toward their untested
  // defaults (mirroring NarratorApplication.SaveSettingsAsync's reset-on-change logic), since a
  // different endpoint or model can't be assumed to share the same negotiated capabilities.
  structuredOutputTier: StructuredOutputTier;
  outputTokenParameter: OutputTokenParameter;
  instructionMessageRole: InstructionMessageRole;
}

export interface TrashItem {
  trashId: string;
  type: 'definition' | 'story';
  originalId: string;
  displayName: string;
  deletedAtUtc: string;
  payload: StoryDefinition | StoryState;
}

export interface DefinitionGeneration {
  refinedStoryPrompt: string;
  suggestedTitle: string;
  initialEventsPrompt: string;
  initialStoryBibleEntries: Array<Omit<StoryBibleEntry, 'id' | 'lastRelevantTurnNumber'>>;
  initialPlannedEvents: ProposedPlannedEvent[];
  initialVictoryConditions: ProposedStoryCondition[];
  initialLossConditions: ProposedStoryCondition[];
}

export interface TurnGeneration {
  turnNumber: number;
  acknowledgedPlayerAction: string | null;
  narration: string;
  suggestedActions: string[];
  relevantStoryBibleEntryIds: string[];
  storyBibleUpdates: StoryBibleUpdate[];
  relevantPlannedEventIds: string[];
  plannedEventUpdates: PlannedEventUpdate[];
  revealedVictoryConditionIds: string[];
  metVictoryConditionIds: string[];
  revealedLossConditionIds: string[];
  metLossConditionIds: string[];
  // The full replacement value for StoryState.storySummary - always returned, never a delta. See the
  // StoryState.storySummary comment above.
  storySummary: string;
}

export const uuid = (): string => crypto.randomUUID();
export const nowIso = (): string => new Date().toISOString();

