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
// instead says how directly and soon to steer toward it. prerequisiteEventIds names other Planned
// Events that must occur first; an id that no longer names a live entry isn't an error - it just means
// that prerequisite already resolved (fulfilled or abandoned) and no longer blocks anything.
export interface PlannedEvent {
  id: string;
  description: string;
  importance: number;
  urgency: number;
  prerequisiteEventIds: string[];
  lastRelevantTurnNumber: number;
}

export type PlannedEventOutcome = 'fulfilled' | 'abandoned';

// Wire-format proposal only - key/prerequisiteKeys never exist on a materialized PlannedEvent. They let
// a batch of proposals (one turn's plannedEventUpdates, or the initial batch proposed alongside a Story
// Definition) reference each other before any of them has a real id: key is a label a proposal invents
// for itself, meaningful only within that one batch; prerequisiteKeys references another proposal's key
// in the same batch. Both are resolved into real ids and discarded once the batch is processed.
export interface ProposedPlannedEvent {
  description: string;
  importance: number;
  urgency: number;
  prerequisiteEventIds: string[];
  key: string | null;
  prerequisiteKeys: string[];
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
  sortOrder: number;
  startedAtUtc: string;
  lastActionAtUtc: string | null;
  turns: StoryTurn[];
}

export interface AppSettings {
  key: 'app';
  baseUrl: string;
  modelId: string;
  apiKey: string;
  requestTimeoutSeconds: number;
  maxOutputTokens: number;
  temperature: number | null;
  topP: number | null;
  reasoningEffort: string;
  recentTurnCount: number;
  maxStoryBibleEntries: number;
  maxPlannedEvents: number;
  plannedEventsWarningPercent: number;
  minSuggestedActions: number;
  maxSuggestedActions: number;
  minParagraphs: number;
  maxParagraphs: number;
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
}

export const uuid = (): string => crypto.randomUUID();
export const nowIso = (): string => new Date().toISOString();

