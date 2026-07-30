import { DbService } from './db.service';
import { LlmService } from './llm.service';
import { StoryBibleEntry } from './models';
import { NarratorService } from './narrator.service';

describe('NarratorService', () => {
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

