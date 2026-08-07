import { vi } from 'vitest';
import { DbService } from './db.service';
import { defaultSettings } from './defaults';
import { LlmService } from './llm.service';
import {
  AppSettings, PlannedEvent, StoryBibleEntry, StoryCondition, StoryDefinition, StoryState,
} from './models';
import { NarratorService } from './narrator.service';

const bibleEntry = (overrides: Partial<StoryBibleEntry> = {}): StoryBibleEntry => ({
  id: 'bible-1',
  category: 'Character',
  name: 'Mara',
  knownFacts: ['A pilot'],
  secretFacts: [],
  importance: 3,
  lastRelevantTurnNumber: 0,
  ...overrides,
});

const plannedEvent = (overrides: Partial<PlannedEvent> = {}): PlannedEvent => ({
  id: 'event-1',
  description: 'A storm rolls in.',
  importance: 3,
  urgency: 3,
  condition: null,
  lastRelevantTurnNumber: 0,
  ...overrides,
});

const condition = (overrides: Partial<StoryCondition> = {}): StoryCondition => ({
  id: 'condition-1',
  description: 'Escape the station.',
  secret: false,
  ...overrides,
});

const definition = (overrides: Partial<StoryDefinition> = {}): StoryDefinition => ({
  id: 'definition-id',
  title: 'Violet Sky',
  storyPrompt: 'A quiet alien world.',
  initialEventsPrompt: '',
  initialStoryBible: [],
  initialPlannedEvents: [],
  initialVictoryConditions: [],
  initialLossConditions: [],
  sortOrder: 0,
  createdAtUtc: new Date().toISOString(),
  updatedAtUtc: new Date().toISOString(),
  ...overrides,
});

const emptyOpeningResponse = {
  turnNumber: 0,
  acknowledgedPlayerAction: null,
  narration: 'You wake beneath a violet sky.',
  suggestedActions: ['Stand up'],
  relevantStoryBibleEntryIds: [],
  storyBibleUpdates: [],
  relevantPlannedEventIds: [],
  plannedEventUpdates: [],
  revealedVictoryConditionIds: [],
  metVictoryConditionIds: [],
  revealedLossConditionIds: [],
  metLossConditionIds: [],
  storySummary: 'You woke beneath a violet sky.',
};

const settingsWith = (overrides: Partial<AppSettings> = {}): AppSettings => ({
  ...defaultSettings(),
  modelId: 'test-model',
  ...overrides,
});

function story(overrides: Partial<StoryState> = {}): StoryState {
  const base: StoryState = {
    id: 'story-1',
    label: 'Violet Sky',
    sourceStoryDefinitionId: 'definition-id',
    definition: {
      title: 'Violet Sky',
      storyPrompt: 'A quiet alien world.',
      initialEventsPrompt: '',
      initialStoryBible: [],
      initialPlannedEvents: [],
      initialVictoryConditions: [],
      initialLossConditions: [],
    },
    currentStoryBible: [],
    currentPlannedEvents: [],
    currentVictoryConditions: [],
    currentLossConditions: [],
    revealedVictoryConditionIds: [],
    metVictoryConditionIds: [],
    revealedLossConditionIds: [],
    metLossConditionIds: [],
    storySummary: '',
    sortOrder: 0,
    startedAtUtc: new Date().toISOString(),
    lastActionAtUtc: null,
    turns: [{
      id: 'turn-1', storyStateId: 'story-1', sequenceNumber: 0, playerAction: null,
      narration: 'Opening scene.', suggestedActions: ['Look around'],
      relevantStoryBibleEntryIds: [], storyBibleUpdates: [],
      relevantPlannedEventIds: [], plannedEventUpdates: [],
      revealedVictoryConditionIds: [], metVictoryConditionIds: [],
      revealedLossConditionIds: [], metLossConditionIds: [],
      completedAtUtc: new Date().toISOString(), modelId: 'test-model',
    }],
  };
  return { ...base, ...overrides };
}

describe('NarratorService', () => {
  describe('startStory', () => {
    it('persists a newly started story with its opening turn', async () => {
      const saved: StoryState[] = [];
      const database = {
        settings: vi.fn(async () => settingsWith()),
        stories: vi.fn(async () => []),
        saveStory: vi.fn(async (value: StoryState) => { saved.push(value); }),
      };
      const llm = { opening: vi.fn(async () => emptyOpeningResponse) };
      const service = new NarratorService(database as unknown as DbService, llm as unknown as LlmService);

      const result = await service.startStory(definition());

      expect(database.saveStory).toHaveBeenCalledOnce();
      expect(saved).toHaveLength(1);
      expect(result.turns[0].narration).toContain('violet sky');
    });

    it('adopts the opening response storySummary as the new story storySummary', async () => {
      const database = {
        settings: vi.fn(async () => settingsWith()),
        stories: vi.fn(async () => []),
        saveStory: vi.fn(),
      };
      const llm = { opening: vi.fn(async () => ({ ...emptyOpeningResponse, storySummary: 'You woke beneath a violet sky.' })) };
      const service = new NarratorService(database as unknown as DbService, llm as unknown as LlmService);

      const result = await service.startStory(definition());

      expect(result.storySummary).toBe('You woke beneath a violet sky.');
    });

    describe('pre-flight limit checks', () => {
      it('throws before calling the provider when the initial Story Bible exceeds current limits', async () => {
        const database = {
          settings: vi.fn(async () => settingsWith({ maxStoryBibleEntries: 0 })),
          stories: vi.fn(async () => []),
          saveStory: vi.fn(),
        };
        const llm = { opening: vi.fn() };
        const service = new NarratorService(database as unknown as DbService, llm as unknown as LlmService);

        await expect(service.startStory(definition({ initialStoryBible: [bibleEntry()] })))
          .rejects.toThrow(/initial Story Bible exceeds current limits/i);
        expect(llm.opening).not.toHaveBeenCalled();
        expect(database.saveStory).not.toHaveBeenCalled();
      });

      it('throws before calling the provider when the initial Planned Events exceed current limits', async () => {
        const database = {
          settings: vi.fn(async () => settingsWith({ maxPlannedEvents: 0 })),
          stories: vi.fn(async () => []),
          saveStory: vi.fn(),
        };
        const llm = { opening: vi.fn() };
        const service = new NarratorService(database as unknown as DbService, llm as unknown as LlmService);

        await expect(service.startStory(definition({ initialPlannedEvents: [plannedEvent()] })))
          .rejects.toThrow(/initial Planned Events exceed current limits/i);
        expect(llm.opening).not.toHaveBeenCalled();
      });

      it('throws before calling the provider when the initial Victory Conditions exceed current limits', async () => {
        const database = {
          settings: vi.fn(async () => settingsWith({ maxConditions: 0 })),
          stories: vi.fn(async () => []),
          saveStory: vi.fn(),
        };
        const llm = { opening: vi.fn() };
        const service = new NarratorService(database as unknown as DbService, llm as unknown as LlmService);

        await expect(service.startStory(definition({ initialVictoryConditions: [condition()] })))
          .rejects.toThrow(/initial Victory Conditions exceed current limits/i);
        expect(llm.opening).not.toHaveBeenCalled();
      });

      it('throws before calling the provider when the initial Loss Conditions exceed current limits', async () => {
        const database = {
          settings: vi.fn(async () => settingsWith({ maxConditions: 0 })),
          stories: vi.fn(async () => []),
          saveStory: vi.fn(),
        };
        const llm = { opening: vi.fn() };
        const service = new NarratorService(database as unknown as DbService, llm as unknown as LlmService);

        await expect(service.startStory(definition({ initialLossConditions: [condition()] })))
          .rejects.toThrow(/initial Loss Conditions exceed current limits/i);
        expect(llm.opening).not.toHaveBeenCalled();
      });
    });

    describe('id remapping', () => {
      it('remaps every id before the opening request, keeps request/response ids aligned, and never shares ids across two playthroughs of the same definition', async () => {
        const database = {
          settings: vi.fn(async () => settingsWith()),
          stories: vi.fn(async () => []),
          saveStory: vi.fn(),
        };
        // Echoes back, as "relevant"/"revealed", exactly the ids the opening request itself carried -
        // proving the model's response is interpreted against the same remapped ids that were sent out,
        // not the original Definition ids.
        const llm = {
          opening: vi.fn(async (_settings: AppSettings, sent: StoryDefinition) => ({
            ...emptyOpeningResponse,
            relevantStoryBibleEntryIds: sent.initialStoryBible.map(x => x.id),
            relevantPlannedEventIds: sent.initialPlannedEvents.map(x => x.id),
            revealedVictoryConditionIds: sent.initialVictoryConditions.map(x => x.id),
            // The loss condition is secret, so it can only ever be reported met, never revealed - see
            // applyConditionTurn's secret-condition rule.
            metLossConditionIds: sent.initialLossConditions.map(x => x.id),
          })),
        };
        const service = new NarratorService(database as unknown as DbService, llm as unknown as LlmService);
        const source = definition({
          initialStoryBible: [bibleEntry({ id: 'original-bible' })],
          initialPlannedEvents: [plannedEvent({ id: 'original-event' })],
          initialVictoryConditions: [condition({ id: 'original-victory' })],
          initialLossConditions: [condition({ id: 'original-loss', secret: true })],
        });

        const first = await service.startStory(source);
        const second = await service.startStory(source);

        // Original Definition ids never survive into a started story.
        expect(first.definition.initialStoryBible[0].id).not.toBe('original-bible');
        expect(first.definition.initialPlannedEvents[0].id).not.toBe('original-event');
        expect(first.definition.initialVictoryConditions[0].id).not.toBe('original-victory');
        expect(first.definition.initialLossConditions[0].id).not.toBe('original-loss');

        // The response's echoed ids resolved cleanly against the remapped lists (no "unknown entry"
        // throw), so both entries survived into the live story state under the new ids.
        expect(first.currentStoryBible.map(x => x.id)).toEqual(first.definition.initialStoryBible.map(x => x.id));
        expect(first.currentPlannedEvents.map(x => x.id)).toEqual(first.definition.initialPlannedEvents.map(x => x.id));
        expect(first.revealedVictoryConditionIds).toEqual(first.definition.initialVictoryConditions.map(x => x.id));
        expect(first.metLossConditionIds).toEqual(first.definition.initialLossConditions.map(x => x.id));

        // Two playthroughs of the same Definition never share an id across any of the four lists.
        expect(first.definition.initialStoryBible[0].id).not.toBe(second.definition.initialStoryBible[0].id);
        expect(first.definition.initialPlannedEvents[0].id).not.toBe(second.definition.initialPlannedEvents[0].id);
        expect(first.definition.initialVictoryConditions[0].id).not.toBe(second.definition.initialVictoryConditions[0].id);
        expect(first.definition.initialLossConditions[0].id).not.toBe(second.definition.initialLossConditions[0].id);
      });
    });
  });

  describe('play', () => {
    it('replaces the story storySummary wholesale with the turn response storySummary', async () => {
      const saved: StoryState[] = [];
      const database = {
        settings: vi.fn(async () => settingsWith()),
        story: vi.fn(async () => story({ storySummary: 'The old summary.' })),
        saveStory: vi.fn(async (value: StoryState) => { saved.push(value); }),
      };
      const llm = {
        turn: vi.fn(async () => ({
          turnNumber: 1,
          acknowledgedPlayerAction: 'Look around',
          narration: 'You look around the room.',
          suggestedActions: ['Open the door'],
          relevantStoryBibleEntryIds: [],
          storyBibleUpdates: [],
          relevantPlannedEventIds: [],
          plannedEventUpdates: [],
          revealedVictoryConditionIds: [],
          metVictoryConditionIds: [],
          revealedLossConditionIds: [],
          metLossConditionIds: [],
          storySummary: 'The new summary, fully replacing the old one.',
        })),
      };
      const service = new NarratorService(database as unknown as DbService, llm as unknown as LlmService);

      const result = await service.play('story-1', 'Look around');

      expect(result.storySummary).toBe('The new summary, fully replacing the old one.');
      expect(saved).toEqual([result]);
    });

    describe('pre-flight limit checks', () => {
      it('throws before calling the provider when the current Story Bible exceeds current limits', async () => {
        const database = {
          settings: vi.fn(async () => settingsWith({ maxStoryBibleEntries: 0 })),
          story: vi.fn(async () => story({ currentStoryBible: [bibleEntry()] })),
        };
        const llm = { turn: vi.fn() };
        const service = new NarratorService(database as unknown as DbService, llm as unknown as LlmService);

        await expect(service.play('story-1', 'Look around')).rejects.toThrow(/Story Bible exceeds current limits/i);
        expect(llm.turn).not.toHaveBeenCalled();
      });

      it('throws before calling the provider when the current Planned Events exceed current limits', async () => {
        const database = {
          settings: vi.fn(async () => settingsWith({ maxPlannedEvents: 0 })),
          story: vi.fn(async () => story({ currentPlannedEvents: [plannedEvent()] })),
        };
        const llm = { turn: vi.fn() };
        const service = new NarratorService(database as unknown as DbService, llm as unknown as LlmService);

        await expect(service.play('story-1', 'Look around')).rejects.toThrow(/Planned Events exceed current limits/i);
        expect(llm.turn).not.toHaveBeenCalled();
      });
    });
  });

  describe('cullDefinition', () => {
    it('culls the initial Story Bible and Planned Events down to current limits and persists the result', async () => {
      const saved: StoryDefinition[] = [];
      const database = {
        settings: vi.fn(async () => settingsWith({ maxStoryBibleEntries: 1, maxPlannedEvents: 1 })),
        saveDefinition: vi.fn(async (value: StoryDefinition) => { saved.push(value); }),
      };
      const service = new NarratorService(database as unknown as DbService, {} as unknown as LlmService);
      const source = definition({
        initialStoryBible: [
          bibleEntry({ id: 'low', importance: 1 }),
          bibleEntry({ id: 'high', importance: 5 }),
        ],
        initialPlannedEvents: [
          plannedEvent({ id: 'low', importance: 1 }),
          plannedEvent({ id: 'high', importance: 5 }),
        ],
      });

      const result = await service.cullDefinition(source);

      expect(result.initialStoryBible.map(x => x.id)).toEqual(['high']);
      expect(result.initialPlannedEvents.map(x => x.id)).toEqual(['high']);
      expect(saved).toEqual([result]);
    });

    it('leaves everything untouched when already within limits', async () => {
      const database = {
        settings: vi.fn(async () => settingsWith()),
        saveDefinition: vi.fn(async () => {}),
      };
      const service = new NarratorService(database as unknown as DbService, {} as unknown as LlmService);
      const source = definition({ initialStoryBible: [bibleEntry()], initialPlannedEvents: [plannedEvent()] });

      const result = await service.cullDefinition(source);

      expect(result.initialStoryBible).toHaveLength(1);
      expect(result.initialPlannedEvents).toHaveLength(1);
    });
  });

  describe('cullStoryState', () => {
    it('culls the current Story Bible and Planned Events down to current limits and persists the result', async () => {
      const saved: StoryState[] = [];
      const database = {
        settings: vi.fn(async () => settingsWith({ maxStoryBibleEntries: 1, maxPlannedEvents: 1 })),
        saveStory: vi.fn(async (value: StoryState) => { saved.push(value); }),
      };
      const service = new NarratorService(database as unknown as DbService, {} as unknown as LlmService);
      const source = story({
        currentStoryBible: [
          bibleEntry({ id: 'low', importance: 1 }),
          bibleEntry({ id: 'high', importance: 5 }),
        ],
        currentPlannedEvents: [
          plannedEvent({ id: 'low', importance: 1 }),
          plannedEvent({ id: 'high', importance: 5 }),
        ],
      });

      const result = await service.cullStoryState(source);

      expect(result.currentStoryBible.map(x => x.id)).toEqual(['high']);
      expect(result.currentPlannedEvents.map(x => x.id)).toEqual(['high']);
      expect(saved).toEqual([result]);
    });
  });

  describe('updateStorySummary', () => {
    it('trims and persists a manually edited story summary', async () => {
      const saved: StoryState[] = [];
      const database = {
        settings: vi.fn(async () => settingsWith()),
        story: vi.fn(async () => story({ storySummary: 'The old summary.' })),
        saveStory: vi.fn(async (value: StoryState) => { saved.push(value); }),
      };
      const service = new NarratorService(database as unknown as DbService, {} as unknown as LlmService);

      const result = await service.updateStorySummary('story-1', '  A manually corrected summary.  ');

      expect(result.storySummary).toBe('A manually corrected summary.');
      expect(saved).toEqual([result]);
    });

    it('throws without saving when the summary exceeds the configured limit', async () => {
      const database = {
        settings: vi.fn(async () => settingsWith({ maxStorySummaryCharacters: 500 })),
        story: vi.fn(async () => story()),
        saveStory: vi.fn(),
      };
      const service = new NarratorService(database as unknown as DbService, {} as unknown as LlmService);

      await expect(service.updateStorySummary('story-1', 'x'.repeat(501)))
        .rejects.toThrow(/story summary exceeds the configured limit/i);
      expect(database.saveStory).not.toHaveBeenCalled();
    });

    it('throws when the story does not exist', async () => {
      const database = {
        settings: vi.fn(async () => settingsWith()),
        story: vi.fn(async () => undefined),
        saveStory: vi.fn(),
      };
      const service = new NarratorService(database as unknown as DbService, {} as unknown as LlmService);

      await expect(service.updateStorySummary('missing-id', 'Anything')).rejects.toThrow(/not found/i);
    });
  });
});
