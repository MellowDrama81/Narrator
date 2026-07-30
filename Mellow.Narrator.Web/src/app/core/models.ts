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

export interface StoryDefinition {
  id: string;
  title: string;
  storyPrompt: string;
  initialEventsPrompt: string;
  initialStoryBible: StoryBibleEntry[];
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
  completedAtUtc: string;
  modelId: string;
}

export interface StoryState {
  id: string;
  label: string;
  sourceStoryDefinitionId: string | null;
  definition: Pick<StoryDefinition, 'title' | 'storyPrompt' | 'initialEventsPrompt' | 'initialStoryBible'>;
  currentStoryBible: StoryBibleEntry[];
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
}

export interface TurnGeneration {
  turnNumber: number;
  acknowledgedPlayerAction: string | null;
  narration: string;
  suggestedActions: string[];
  relevantStoryBibleEntryIds: string[];
  storyBibleUpdates: StoryBibleUpdate[];
}

export const uuid = (): string => crypto.randomUUID();
export const nowIso = (): string => new Date().toISOString();

