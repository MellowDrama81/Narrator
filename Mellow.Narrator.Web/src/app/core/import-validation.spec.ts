import { validateImportedDefinition, validateImportedState } from './import-validation';
import { AppSettings, StoryDefinition, StoryState, StoryTurn } from './models';
import { defaultSettings } from './defaults';

const limits: AppSettings = defaultSettings();

const definition = (overrides: Partial<StoryDefinition> = {}): StoryDefinition => ({
  id: 'def-1',
  title: 'The Lighthouse',
  storyPrompt: 'A keeper guards a light that must never go out.',
  initialEventsPrompt: '',
  initialStoryBible: [
    { id: 'bible-1', category: 'Place', name: 'The Lighthouse', knownFacts: [], secretFacts: [], importance: 3, lastRelevantTurnNumber: 0 },
  ],
  initialPlannedEvents: [
    { id: 'event-1', description: 'The storm arrives.', importance: 3, urgency: 3, condition: null, lastRelevantTurnNumber: 0 },
  ],
  initialVictoryConditions: [{ id: 'victory-1', description: 'Survive the storm.', secret: false }],
  initialLossConditions: [{ id: 'loss-1', description: 'The light goes out.', secret: true }],
  sortOrder: 0,
  createdAtUtc: '2026-01-01T00:00:00.000Z',
  updatedAtUtc: '2026-01-01T00:00:00.000Z',
  ...overrides,
});

const turn = (overrides: Partial<StoryTurn> = {}): StoryTurn => ({
  id: 'turn-1',
  storyStateId: 'story-1',
  sequenceNumber: 0,
  playerAction: null,
  narration: 'The lamp is lit for the night watch.',
  suggestedActions: ['Check the oil.', 'Watch the horizon.'],
  relevantStoryBibleEntryIds: [],
  storyBibleUpdates: [],
  relevantPlannedEventIds: [],
  plannedEventUpdates: [],
  revealedVictoryConditionIds: [],
  metVictoryConditionIds: [],
  revealedLossConditionIds: [],
  metLossConditionIds: [],
  completedAtUtc: '2026-01-01T00:00:00.000Z',
  modelId: 'test-model',
  ...overrides,
});

const state = (overrides: Partial<StoryState> = {}, turns: StoryTurn[] = [turn()]): StoryState => ({
  id: 'story-1',
  label: 'A Long Night',
  sourceStoryDefinitionId: 'def-1',
  definition: {
    title: definition().title,
    storyPrompt: definition().storyPrompt,
    initialEventsPrompt: '',
    initialStoryBible: definition().initialStoryBible,
    initialPlannedEvents: definition().initialPlannedEvents,
    initialVictoryConditions: definition().initialVictoryConditions,
    initialLossConditions: definition().initialLossConditions,
  },
  currentStoryBible: definition().initialStoryBible,
  currentPlannedEvents: definition().initialPlannedEvents,
  currentVictoryConditions: definition().initialVictoryConditions,
  currentLossConditions: definition().initialLossConditions,
  revealedVictoryConditionIds: [],
  metVictoryConditionIds: [],
  revealedLossConditionIds: [],
  metLossConditionIds: [],
  storySummary: '',
  sortOrder: 0,
  startedAtUtc: '2026-01-01T00:00:00.000Z',
  lastActionAtUtc: null,
  turns,
  ...overrides,
});

describe('validateImportedDefinition', () => {
  it('accepts a well-formed definition', () => {
    expect(validateImportedDefinition(definition(), limits)).toBeNull();
  });

  it('rejects a missing id', () => {
    expect(validateImportedDefinition(definition({ id: '' }), limits)).toMatch(/ID is invalid/);
  });

  it('rejects an empty title', () => {
    expect(validateImportedDefinition(definition({ title: '   ' }), limits)).toMatch(/title/);
  });

  it('rejects an empty story prompt', () => {
    expect(validateImportedDefinition(definition({ storyPrompt: '' }), limits)).toMatch(/Story Prompt/);
  });

  it('rejects a title that exceeds the configured limit', () => {
    expect(validateImportedDefinition(definition({ title: 'x'.repeat(limits.maxStoryTitleCharacters + 1) }), limits))
      .toMatch(/title/);
  });

  it('rejects duplicate Story Bible entry ids', () => {
    const bible = definition().initialStoryBible;
    const duplicated = [...bible, { ...bible[0] }];
    expect(validateImportedDefinition(definition({ initialStoryBible: duplicated }), limits))
      .toMatch(/Story Bible entry IDs/);
  });

  it('rejects a Story Bible entry missing its name', () => {
    const bible = [{ ...definition().initialStoryBible[0], name: '' }];
    expect(validateImportedDefinition(definition({ initialStoryBible: bible }), limits)).toMatch(/category or name/);
  });

  it('rejects a Planned Event with an out-of-range importance', () => {
    const events = [{ ...definition().initialPlannedEvents[0], importance: 9 }];
    expect(validateImportedDefinition(definition({ initialPlannedEvents: events }), limits)).toMatch(/importance/);
  });

  it('rejects duplicate condition ids', () => {
    const victory = definition().initialVictoryConditions;
    const duplicated = [...victory, { ...victory[0] }];
    expect(validateImportedDefinition(definition({ initialVictoryConditions: duplicated }), limits))
      .toMatch(/Victory Conditions IDs/);
  });

  it('rejects a condition missing its description', () => {
    const loss = [{ ...definition().initialLossConditions[0], description: '' }];
    expect(validateImportedDefinition(definition({ initialLossConditions: loss }), limits)).toMatch(/description/);
  });

  it('rejects a non-object value', () => {
    expect(validateImportedDefinition(null, limits)).toMatch(/does not contain/);
    expect(validateImportedDefinition(undefined, limits)).toMatch(/does not contain/);
  });
});

describe('validateImportedState', () => {
  it('accepts a well-formed single-turn story', () => {
    expect(validateImportedState(state(), limits)).toBeNull();
  });

  it('rejects a missing id', () => {
    expect(validateImportedState(state({ id: '' }), limits)).toMatch(/Story ID is invalid/);
  });

  it('rejects an empty label', () => {
    expect(validateImportedState(state({ label: ' ' }), limits)).toMatch(/label/);
  });

  it('rejects a story with no turns', () => {
    expect(validateImportedState(state({ turns: [] }), limits)).toMatch(/no turns/);
  });

  it('accepts a contiguous multi-turn story', () => {
    const turns = [
      turn({ id: 'turn-0', sequenceNumber: 0, playerAction: null }),
      turn({ id: 'turn-1', sequenceNumber: 1, playerAction: 'Check the oil.' }),
      turn({ id: 'turn-2', sequenceNumber: 2, playerAction: 'Watch the horizon.' }),
    ];
    expect(validateImportedState(state({ turns }), limits)).toBeNull();
  });

  it('rejects a gap in turn sequence numbers', () => {
    const turns = [
      turn({ id: 'turn-0', sequenceNumber: 0 }),
      turn({ id: 'turn-1', sequenceNumber: 2 }),
    ];
    expect(validateImportedState(state({ turns }), limits)).toMatch(/not contiguous/);
  });

  it('rejects duplicate turn ids', () => {
    const turns = [
      turn({ id: 'turn-0', sequenceNumber: 0 }),
      turn({ id: 'turn-0', sequenceNumber: 1 }),
    ];
    expect(validateImportedState(state({ turns }), limits)).toMatch(/Turn identities/);
  });

  it('rejects a turn whose storyStateId does not match the story', () => {
    const turns = [turn({ storyStateId: 'some-other-story' })];
    expect(validateImportedState(state({ turns }), limits)).toMatch(/Turn identities/);
  });

  it('rejects an opening turn with a player action', () => {
    const turns = [turn({ playerAction: 'Not allowed on turn zero.' })];
    expect(validateImportedState(state({ turns }), limits)).toMatch(/opening turn/);
  });

  it('rejects a turn with empty narration', () => {
    const turns = [turn({ narration: '' })];
    expect(validateImportedState(state({ turns }), limits)).toMatch(/narration/);
  });

  it('rejects too many suggested actions', () => {
    const turns = [turn({ suggestedActions: Array.from({ length: limits.maxSuggestedActions + 1 }, (_, i) => `Action ${i}`) })];
    expect(validateImportedState(state({ turns }), limits)).toMatch(/too many suggested actions/);
  });

  it('rejects a revealed-condition id that does not exist in the current conditions', () => {
    expect(validateImportedState(state({ revealedVictoryConditionIds: ['missing-id'] }), limits))
      .toMatch(/revealed Victory Condition/);
  });

  it('rejects a non-object value', () => {
    expect(validateImportedState(null, limits)).toMatch(/does not contain/);
  });
});
