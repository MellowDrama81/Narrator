import { vi } from 'vitest';
import { DbService } from './db.service';
import { defaultSettings } from './defaults';
import { LlmService } from './llm.service';
import { StoryBibleEntry, StoryDefinition, StoryState } from './models';
import { NarratorService } from './narrator.service';

describe('NarratorService', () => {
  it('persists a newly started story with its opening turn', async () => {
    const saved: StoryState[] = [];
    const database = {
      settings: vi.fn(async () => ({
        ...defaultSettings(),
        modelId: 'test-model',
      })),
      stories: vi.fn(async () => []),
      saveStory: vi.fn(async (story: StoryState) => {
        saved.push(story);
      }),
    };
    const llm = {
      opening: vi.fn(async () => ({
        turnNumber: 0,
        acknowledgedPlayerAction: null,
        narration: 'You wake beneath a violet sky.',
        suggestedActions: ['Stand up'],
        relevantStoryBibleEntryIds: [],
        storyBibleUpdates: [],
      })),
    };
    const service = new NarratorService(database as unknown as DbService, llm as unknown as LlmService);
    const definition: StoryDefinition = {
      id: 'definition-id',
      title: 'Violet Sky',
      storyPrompt: 'A quiet alien world.',
      initialEventsPrompt: '',
      initialStoryBible: [],
      sortOrder: 0,
      createdAtUtc: new Date().toISOString(),
      updatedAtUtc: new Date().toISOString(),
    };

    const story = await service.startStory(definition);

    expect(database.saveStory).toHaveBeenCalledOnce();
    expect(saved).toHaveLength(1);
    expect(story.turns[0].narration).toContain('violet sky');
  });

  it('applies incremental Story Bible updates and relevance', () => {
    const service = new NarratorService({} as DbService, {} as LlmService);
    const original: StoryBibleEntry[] = [{
      id: 'existing', category: 'Character', name: 'Mara', knownFacts: ['A pilot'],
      secretFacts: [], importance: 4, lastRelevantTurnNumber: 0,
    }];

    const result = service.applyBible(original, [{
      operation: 'add', entryId: null,
      entry: { category: 'Place', name: 'The Glass Port', knownFacts: ['A ruined station'], secretFacts: [], importance: 3 },
    }], ['existing'], 2, 10);

    expect(result).toHaveLength(2);
    expect(result.find(x => x.id === 'existing')?.lastRelevantTurnNumber).toBe(2);
    expect(result.find(x => x.name === 'The Glass Port')).toBeTruthy();
    expect(original).toHaveLength(1);
  });

  it('culls least important entries to the configured maximum', () => {
    const service = new NarratorService({} as DbService, {} as LlmService);
    const values: StoryBibleEntry[] = [1, 2, 3].map(importance => ({
      id: String(importance), category: 'Fact', name: String(importance), knownFacts: ['fact'],
      secretFacts: [], importance, lastRelevantTurnNumber: importance,
    }));

    const result = service.applyBible(values, [], [], 4, 2);

    expect(result.map(x => x.importance)).toEqual([3, 2]);
  });
});
