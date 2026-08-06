import {
  applyStoryBible, cullBibleToLimits, isApproachingBibleLimits, isWithinBibleLimits, StoryBibleLimits, validateBibleEntry,
} from './story-bible';
import { StoryBibleEntry, StoryBibleUpdate } from './models';

const limits = (overrides: Partial<StoryBibleLimits> = {}): StoryBibleLimits => ({
  maxEntries: 50,
  maxEntryCharacters: 4000,
  maxTotalCharacters: 60000,
  ...overrides,
});

const proposal = (overrides: Partial<StoryBibleUpdate['entry']> = {}): StoryBibleUpdate['entry'] => ({
  category: 'Character',
  name: 'Mara',
  knownFacts: ['A pilot'],
  secretFacts: [],
  importance: 3,
  ...overrides,
});

const add = (overrides: Partial<StoryBibleUpdate['entry']> = {}): StoryBibleUpdate => ({
  operation: 'add', entryId: null, entry: proposal(overrides),
});

const entry = (overrides: Partial<StoryBibleEntry> = {}): StoryBibleEntry => ({
  id: 'entry-1',
  category: 'Character',
  name: 'Mara',
  knownFacts: ['A pilot'],
  secretFacts: [],
  importance: 3,
  lastRelevantTurnNumber: 0,
  ...overrides,
});

describe('validateBibleEntry', () => {
  it('accepts a valid entry', () => {
    expect(validateBibleEntry(entry())).toBeNull();
  });

  it('rejects an empty category', () => {
    expect(validateBibleEntry(entry({ category: '   ' }))).toMatch(/category is empty/i);
  });

  it('rejects an empty name', () => {
    expect(validateBibleEntry(entry({ name: '' }))).toMatch(/name is empty/i);
  });

  it('rejects an entry with both knownFacts and secretFacts empty', () => {
    expect(validateBibleEntry(entry({ knownFacts: [], secretFacts: [] }))).toMatch(/at least one known or secret fact/i);
  });

  it('accepts an entry with only secretFacts populated', () => {
    expect(validateBibleEntry(entry({ knownFacts: [], secretFacts: ['A hidden past'] }))).toBeNull();
  });

  it('rejects an empty individual known fact', () => {
    expect(validateBibleEntry(entry({ knownFacts: ['A pilot', '   '] }))).toMatch(/empty fact/i);
  });

  it('rejects an empty individual secret fact', () => {
    expect(validateBibleEntry(entry({ knownFacts: [], secretFacts: ['A pilot', ''] }))).toMatch(/empty fact/i);
  });

  it('rejects importance below 1', () => {
    expect(validateBibleEntry(entry({ importance: 0 }))).toMatch(/importance must be from 1 to 5/i);
  });

  it('rejects importance above 5', () => {
    expect(validateBibleEntry(entry({ importance: 6 }))).toMatch(/importance must be from 1 to 5/i);
  });
});

describe('applyStoryBible', () => {
  it('adds a new entry and marks it relevant', () => {
    const result = applyStoryBible([], [add()], [], 1, limits());
    expect(result).toHaveLength(1);
    expect(result[0].name).toBe('Mara');
    expect(result[0].lastRelevantTurnNumber).toBe(1);
  });

  it('applies incremental updates and stamps relevance on existing entries', () => {
    const original = [entry({ id: 'existing' })];
    const result = applyStoryBible(
      original,
      [add({ category: 'Place', name: 'The Glass Port', knownFacts: ['A ruined station'], secretFacts: [] })],
      ['existing'],
      2,
      limits(),
    );
    expect(result).toHaveLength(2);
    expect(result.find(x => x.id === 'existing')?.lastRelevantTurnNumber).toBe(2);
    expect(result.find(x => x.name === 'The Glass Port')).toBeTruthy();
    expect(original).toHaveLength(1);
  });

  it('removes an entry', () => {
    const original = [entry()];
    const updates: StoryBibleUpdate[] = [{ operation: 'remove', entryId: 'entry-1', entry: null }];
    expect(applyStoryBible(original, updates, [], 1, limits())).toHaveLength(0);
  });

  it('replaces an entry', () => {
    const original = [entry()];
    const updates: StoryBibleUpdate[] = [{
      operation: 'replace', entryId: 'entry-1', entry: proposal({ name: 'Mara Voss', importance: 5 }),
    }];
    const result = applyStoryBible(original, updates, [], 1, limits());
    expect(result).toHaveLength(1);
    expect(result[0].name).toBe('Mara Voss');
    expect(result[0].importance).toBe(5);
    expect(result[0].lastRelevantTurnNumber).toBe(1);
  });

  it('trims whitespace on add', () => {
    const result = applyStoryBible([], [add({ category: '  Character  ', name: '  Mara  ', knownFacts: ['  A pilot  '] })], [], 1, limits());
    expect(result[0].category).toBe('Character');
    expect(result[0].name).toBe('Mara');
    expect(result[0].knownFacts).toEqual(['A pilot']);
  });

  it('throws when an add entry is invalid', () => {
    expect(() => applyStoryBible([], [add({ name: '' })], [], 1, limits())).toThrow(/name is empty/i);
  });

  it('throws when a replace entry is invalid', () => {
    const original = [entry()];
    const updates: StoryBibleUpdate[] = [{ operation: 'replace', entryId: 'entry-1', entry: proposal({ importance: 0 }) }];
    expect(() => applyStoryBible(original, updates, [], 1, limits())).toThrow(/importance must be from 1 to 5/i);
  });

  it('throws when an update references an unknown entryId', () => {
    const updates: StoryBibleUpdate[] = [{ operation: 'remove', entryId: 'missing', entry: null }];
    expect(() => applyStoryBible([], updates, [], 1, limits())).toThrow(/unknown entry/i);
  });

  it('throws when updating the same entry twice in one batch', () => {
    const original = [entry()];
    const updates: StoryBibleUpdate[] = [
      { operation: 'replace', entryId: 'entry-1', entry: proposal() },
      { operation: 'remove', entryId: 'entry-1', entry: null },
    ];
    expect(() => applyStoryBible(original, updates, [], 1, limits())).toThrow(/more than once/i);
  });

  it('throws when removing an entry that is also marked relevant', () => {
    const original = [entry()];
    const updates: StoryBibleUpdate[] = [{ operation: 'remove', entryId: 'entry-1', entry: null }];
    expect(() => applyStoryBible(original, updates, ['entry-1'], 1, limits())).toThrow(/cannot also be relevant/i);
  });

  it('throws when removing an entry that also carries a replacement entry', () => {
    const original = [entry()];
    const updates: StoryBibleUpdate[] = [{ operation: 'remove', entryId: 'entry-1', entry: proposal() }];
    expect(() => applyStoryBible(original, updates, [], 1, limits())).toThrow(/cannot also be relevant/i);
  });

  it('throws when an id is marked relevant but does not exist', () => {
    expect(() => applyStoryBible([], [], ['missing'], 1, limits())).toThrow(/unknown Story Bible entry/i);
  });

  describe('culling by character budget', () => {
    it('evicts an individual entry whose serialized size exceeds the per-entry budget', () => {
      const oversized = entry({ id: 'huge', knownFacts: ['x'.repeat(5000)] });
      const fine = entry({ id: 'fine' });
      const result = applyStoryBible([oversized, fine], [], [], 1, limits({ maxEntryCharacters: 500 }));
      expect(result.map(x => x.id)).toEqual(['fine']);
    });

    it('evicts lowest-importance/least-recent entries while total serialized size exceeds the budget', () => {
      const original = [
        entry({ id: 'low', importance: 1, lastRelevantTurnNumber: 5, knownFacts: ['a'.repeat(100)] }),
        entry({ id: 'high-old', importance: 4, lastRelevantTurnNumber: 1, knownFacts: ['b'.repeat(100)] }),
        entry({ id: 'high-new', importance: 4, lastRelevantTurnNumber: 9, knownFacts: ['c'.repeat(100)] }),
      ];
      const totalSize = JSON.stringify(original).length;
      const result = applyStoryBible(original, [], [], 10, limits({ maxTotalCharacters: totalSize - 1 }));
      expect(result.map(x => x.id)).toEqual(['high-new', 'high-old']);
    });

    it('evicts least important entries to stay within the configured entry count', () => {
      const values: StoryBibleEntry[] = [1, 2, 3].map(importance => entry({
        id: String(importance), name: String(importance), importance, lastRelevantTurnNumber: importance,
      }));
      const result = applyStoryBible(values, [], [], 4, limits({ maxEntries: 2 }));
      expect(result.map(x => x.importance)).toEqual([3, 2]);
    });
  });
});

describe('isWithinBibleLimits', () => {
  it('is true when every budget is satisfied', () => {
    expect(isWithinBibleLimits([entry()], limits())).toBe(true);
  });

  it('is false when the entry count exceeds the maximum', () => {
    expect(isWithinBibleLimits([entry({ id: 'a' }), entry({ id: 'b' })], limits({ maxEntries: 1 }))).toBe(false);
  });

  it('is false when an entry exceeds the per-entry character budget', () => {
    expect(isWithinBibleLimits([entry({ knownFacts: ['x'.repeat(5000)] })], limits({ maxEntryCharacters: 500 }))).toBe(false);
  });

  it('is false when total serialized size exceeds the total character budget', () => {
    const values = [entry({ id: 'a' }), entry({ id: 'b' })];
    expect(isWithinBibleLimits(values, limits({ maxTotalCharacters: JSON.stringify(values).length - 1 }))).toBe(false);
  });
});

describe('cullBibleToLimits', () => {
  it('returns every entry unchanged and reports nothing removed when already within limits', () => {
    const entries = [entry({ id: 'a' }), entry({ id: 'b' })];
    const result = cullBibleToLimits(entries, limits());
    expect(result.entries.map(x => x.id)).toEqual(['a', 'b']);
    expect(result.removed).toEqual([]);
  });

  it('reports the same entries applyStoryBible would evict for an entry-count overflow', () => {
    const values = [1, 2, 3].map(importance => entry({
      id: String(importance), name: String(importance), importance, lastRelevantTurnNumber: importance,
    }));
    const result = cullBibleToLimits(values, limits({ maxEntries: 2 }));
    expect(result.entries.map(x => x.importance)).toEqual([3, 2]);
    expect(result.removed.map(x => x.id)).toEqual(['1']);
  });

  it('reports an oversized entry as removed', () => {
    const oversized = entry({ id: 'huge', knownFacts: ['x'.repeat(5000)] });
    const fine = entry({ id: 'fine' });
    const result = cullBibleToLimits([oversized, fine], limits({ maxEntryCharacters: 500 }));
    expect(result.entries.map(x => x.id)).toEqual(['fine']);
    expect(result.removed.map(x => x.id)).toEqual(['huge']);
  });

  it('does not mutate the input array', () => {
    const original = [entry({ id: 'a' }), entry({ id: 'b' })];
    cullBibleToLimits(original, limits({ maxEntries: 1 }));
    expect(original).toHaveLength(2);
  });
});

describe('isApproachingBibleLimits', () => {
  it('is false when well under every limit', () => {
    expect(isApproachingBibleLimits([entry()], limits(), 80)).toBe(false);
  });

  it('is true when entry count is at or above the warning threshold', () => {
    const values = [entry({ id: 'a' }), entry({ id: 'b' })];
    expect(isApproachingBibleLimits(values, limits({ maxEntries: 2 }), 80)).toBe(true);
  });

  it('is true when the largest entry is at or above the warning threshold of the per-entry budget', () => {
    const values = [entry({ knownFacts: ['x'.repeat(400)] })];
    const entryCharacters = JSON.stringify(values[0]).length;
    expect(isApproachingBibleLimits(values, limits({ maxEntryCharacters: Math.floor(entryCharacters / 0.8) }), 80)).toBe(true);
  });

  it('is true when total serialized size is at or above the warning threshold of the total budget', () => {
    const values = [entry({ id: 'a' }), entry({ id: 'b' })];
    const totalCharacters = JSON.stringify(values).length;
    expect(isApproachingBibleLimits(values, limits({ maxTotalCharacters: Math.floor(totalCharacters / 0.8) }), 80)).toBe(true);
  });

  it('reports nothing approaching when there are no entries', () => {
    expect(isApproachingBibleLimits([], limits(), 80)).toBe(false);
  });
});
