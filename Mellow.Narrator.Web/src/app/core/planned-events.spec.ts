import {
  applyPlannedEvents, cullPlannedEventsToLimits, isApproachingPlannedEventLimits, isWithinPlannedEventLimits,
  MANDATORY_IMPORTANCE, PlannedEventLimits, plannedEventCapacity, resolveInitialPlannedEvents,
} from './planned-events';
import { PlannedEvent, PlannedEventUpdate, ProposedPlannedEvent } from './models';

const limits = (overrides: Partial<PlannedEventLimits> = {}): PlannedEventLimits => ({
  maxEntries: 50,
  maxEntryCharacters: 4000,
  maxTotalCharacters: 60000,
  maxDescriptionCharacters: 1000,
  maxConditionCharacters: 500,
  ...overrides,
});

const proposal = (overrides: Partial<ProposedPlannedEvent> = {}): ProposedPlannedEvent => ({
  description: 'The lighthouse keeper vanishes.',
  importance: 3,
  urgency: 3,
  condition: null,
  ...overrides,
});

const add = (overrides: Partial<ProposedPlannedEvent> = {}): PlannedEventUpdate => ({
  operation: 'add', entryId: null, outcome: null, entry: proposal(overrides),
});

const event = (overrides: Partial<PlannedEvent> = {}): PlannedEvent => ({
  id: 'event-1',
  description: 'The lighthouse keeper vanishes.',
  importance: 3,
  urgency: 3,
  condition: null,
  lastRelevantTurnNumber: 0,
  ...overrides,
});

describe('applyPlannedEvents', () => {
  it('adds a new event and marks it relevant', () => {
    const result = applyPlannedEvents([], [add()], [], 1, limits());
    expect(result).toHaveLength(1);
    expect(result[0].description).toBe('The lighthouse keeper vanishes.');
    expect(result[0].lastRelevantTurnNumber).toBe(1);
  });

  it('removes an event when fulfilled', () => {
    const original = [event()];
    const updates: PlannedEventUpdate[] = [{ operation: 'remove', entryId: 'event-1', outcome: 'fulfilled', entry: null }];
    expect(applyPlannedEvents(original, updates, [], 1, limits())).toHaveLength(0);
  });

  it('throws when removing without an outcome', () => {
    const original = [event()];
    const updates: PlannedEventUpdate[] = [{ operation: 'remove', entryId: 'event-1', outcome: null, entry: null }];
    expect(() => applyPlannedEvents(original, updates, [], 1, limits())).toThrow(/fulfilled or abandoned/i);
  });

  it('throws when removing an event that is also marked relevant', () => {
    const original = [event()];
    const updates: PlannedEventUpdate[] = [{ operation: 'remove', entryId: 'event-1', outcome: 'fulfilled', entry: null }];
    expect(() => applyPlannedEvents(original, updates, ['event-1'], 1, limits())).toThrow(/cannot also be relevant/i);
  });

  it('throws when updating the same entry twice in one batch', () => {
    const original = [event()];
    const updates: PlannedEventUpdate[] = [
      { operation: 'replace', entryId: 'event-1', outcome: null, entry: proposal() },
      { operation: 'remove', entryId: 'event-1', outcome: 'fulfilled', entry: null },
    ];
    expect(() => applyPlannedEvents(original, updates, [], 1, limits())).toThrow(/more than once/i);
  });

  it('throws when an update references an unknown entryId', () => {
    const updates: PlannedEventUpdate[] = [{ operation: 'remove', entryId: 'missing', outcome: 'fulfilled', entry: null }];
    expect(() => applyPlannedEvents([], updates, [], 1, limits())).toThrow(/unknown entry/i);
  });

  describe('character-budget validation', () => {
    it('rejects an oversized description on add', () => {
      const updates: PlannedEventUpdate[] = [add({ description: 'x'.repeat(1001) })];
      expect(() => applyPlannedEvents([], updates, [], 1, limits({ maxDescriptionCharacters: 1000 })))
        .toThrow(/description exceeds the configured limit/i);
    });

    it('rejects an oversized condition on add', () => {
      const updates: PlannedEventUpdate[] = [add({ condition: 'x'.repeat(501) })];
      expect(() => applyPlannedEvents([], updates, [], 1, limits({ maxConditionCharacters: 500 })))
        .toThrow(/condition exceeds the configured limit/i);
    });

    it('rejects an oversized description on replace', () => {
      const original = [event()];
      const updates: PlannedEventUpdate[] = [{
        operation: 'replace', entryId: 'event-1', outcome: null, entry: proposal({ description: 'x'.repeat(1001) }),
      }];
      expect(() => applyPlannedEvents(original, updates, [], 1, limits({ maxDescriptionCharacters: 1000 })))
        .toThrow(/description exceeds the configured limit/i);
    });

    it('rejects an oversized condition on replace', () => {
      const original = [event()];
      const updates: PlannedEventUpdate[] = [{
        operation: 'replace', entryId: 'event-1', outcome: null, entry: proposal({ condition: 'x'.repeat(501) }),
      }];
      expect(() => applyPlannedEvents(original, updates, [], 1, limits({ maxConditionCharacters: 500 })))
        .toThrow(/condition exceeds the configured limit/i);
    });

    it('allows a condition within the configured limit', () => {
      const updates: PlannedEventUpdate[] = [add({ condition: 'x'.repeat(500) })];
      const result = applyPlannedEvents([], updates, [], 1, limits({ maxConditionCharacters: 500 }));
      expect(result[0].condition).toHaveLength(500);
    });
  });

  describe('mandatory events (importance 5)', () => {
    it('rejects abandoning a mandatory event', () => {
      const original = [event({ importance: MANDATORY_IMPORTANCE })];
      const updates: PlannedEventUpdate[] = [{ operation: 'remove', entryId: 'event-1', outcome: 'abandoned', entry: null }];
      expect(() => applyPlannedEvents(original, updates, [], 1, limits())).toThrow(/mandatory/i);
    });

    it('allows fulfilling a mandatory event', () => {
      const original = [event({ importance: MANDATORY_IMPORTANCE })];
      const updates: PlannedEventUpdate[] = [{ operation: 'remove', entryId: 'event-1', outcome: 'fulfilled', entry: null }];
      expect(applyPlannedEvents(original, updates, [], 1, limits())).toHaveLength(0);
    });

    it('rejects demoting a mandatory event via replace', () => {
      const original = [event({ importance: MANDATORY_IMPORTANCE })];
      const updates: PlannedEventUpdate[] = [{
        operation: 'replace', entryId: 'event-1', outcome: null, entry: proposal({ importance: 4 }),
      }];
      expect(() => applyPlannedEvents(original, updates, [], 1, limits())).toThrow(/cannot be reduced/i);
    });

    it('never culls a mandatory event by count even under a tight entry-count limit', () => {
      const original = [event({ importance: MANDATORY_IMPORTANCE })];
      const result = applyPlannedEvents(original, [], [], 1, limits({ maxEntries: 1 }));
      expect(result).toHaveLength(1);
    });

    it('throws when more mandatory events exist than the configured count limit', () => {
      const original = [event({ id: 'a', importance: MANDATORY_IMPORTANCE }), event({ id: 'b', importance: MANDATORY_IMPORTANCE })];
      expect(() => applyPlannedEvents(original, [], [], 1, limits({ maxEntries: 1 }))).toThrow(/too many mandatory/i);
    });

    it('evicts a mandatory event whose own serialized size exceeds the per-entry budget', () => {
      const oversized = event({ id: 'huge', importance: MANDATORY_IMPORTANCE, condition: 'x'.repeat(4000) });
      const fine = event({ id: 'fine' });
      const result = applyPlannedEvents(
        [oversized, fine], [], [], 1, limits({ maxEntryCharacters: 500, maxConditionCharacters: 4000 }),
      );
      expect(result.map(x => x.id)).toEqual(['fine']);
    });

    it('throws when the surviving mandatory events alone exceed the total character budget', () => {
      const original = [event({ importance: MANDATORY_IMPORTANCE })];
      const totalSize = JSON.stringify(original).length;
      expect(() => applyPlannedEvents(original, [], [], 1, limits({ maxTotalCharacters: totalSize - 1 })))
        .toThrow(/mandatory Planned Events exceed the configured total character limit/i);
    });
  });

  describe('condition normalization', () => {
    it('normalizes a null condition on add', () => {
      const result = applyPlannedEvents([], [add({ condition: null })], [], 1, limits());
      expect(result[0].condition).toBeNull();
    });

    it('normalizes an empty condition to null on add', () => {
      const result = applyPlannedEvents([], [add({ condition: '' })], [], 1, limits());
      expect(result[0].condition).toBeNull();
    });

    it('normalizes a whitespace-only condition to null on add', () => {
      const result = applyPlannedEvents([], [add({ condition: '   ' })], [], 1, limits());
      expect(result[0].condition).toBeNull();
    });

    it('trims a condition on add', () => {
      const result = applyPlannedEvents([], [add({ condition: '  the storm must pass  ' })], [], 1, limits());
      expect(result[0].condition).toBe('the storm must pass');
    });

    it('normalizes and trims a condition on replace', () => {
      const original = [event({ condition: 'old condition' })];
      const updates: PlannedEventUpdate[] = [{
        operation: 'replace', entryId: 'event-1', outcome: null, entry: proposal({ condition: '  new condition  ' }),
      }];
      const result = applyPlannedEvents(original, updates, [], 1, limits());
      expect(result[0].condition).toBe('new condition');
    });

    it('normalizes an empty condition to null on replace', () => {
      const original = [event({ condition: 'old condition' })];
      const updates: PlannedEventUpdate[] = [{
        operation: 'replace', entryId: 'event-1', outcome: null, entry: proposal({ condition: '' }),
      }];
      const result = applyPlannedEvents(original, updates, [], 1, limits());
      expect(result[0].condition).toBeNull();
    });
  });

  describe('culling by character budget', () => {
    it('evicts an individual entry whose serialized size exceeds the per-entry budget', () => {
      const oversized = event({ id: 'huge', condition: 'x'.repeat(4000) });
      const fine = event({ id: 'fine' });
      const result = applyPlannedEvents(
        [oversized, fine], [], [], 1, limits({ maxEntryCharacters: 500, maxConditionCharacters: 4000 }),
      );
      expect(result.map(x => x.id)).toEqual(['fine']);
    });

    it('evicts lowest-importance/least-recent non-mandatory entries while total serialized size exceeds the budget', () => {
      const original = [
        event({ id: 'low', importance: 1, lastRelevantTurnNumber: 5, condition: 'a'.repeat(100) }),
        event({ id: 'high-old', importance: 4, lastRelevantTurnNumber: 1, condition: 'b'.repeat(100) }),
        event({ id: 'high-new', importance: 4, lastRelevantTurnNumber: 9, condition: 'c'.repeat(100) }),
      ];
      const totalSize = JSON.stringify(original).length;
      const result = applyPlannedEvents(
        original, [], [], 10, limits({ maxTotalCharacters: totalSize - 1, maxConditionCharacters: 100 }),
      );
      expect(result.map(x => x.id)).toEqual(['high-new', 'high-old']);
    });

    it('never evicts a mandatory entry while trimming for total size, even if it stays over budget', () => {
      const mandatory = event({ id: 'mandatory', importance: MANDATORY_IMPORTANCE });
      const other = event({ id: 'other', importance: 1 });
      const totalSize = JSON.stringify([mandatory, other]).length;
      const result = applyPlannedEvents(
        [mandatory, other], [], [], 1, limits({ maxTotalCharacters: totalSize - 1 }),
      );
      expect(result.map(x => x.id)).toEqual(['mandatory']);
    });

    it('evicts least important entries to stay within the configured entry count', () => {
      const original = [
        event({ id: 'low', importance: 1, lastRelevantTurnNumber: 5 }),
        event({ id: 'high-old', importance: 4, lastRelevantTurnNumber: 1 }),
        event({ id: 'high-new', importance: 4, lastRelevantTurnNumber: 9 }),
      ];
      const result = applyPlannedEvents(original, [], [], 10, limits({ maxEntries: 2 }));
      expect(result.map(entry => entry.id)).toEqual(['high-new', 'high-old']);
    });
  });
});

describe('resolveInitialPlannedEvents', () => {
  it('resolves a batch of proposals into real Planned Events', () => {
    const result = resolveInitialPlannedEvents([
      proposal({ description: 'A storm rolls in.' }),
      proposal({ description: 'The keeper vanishes.' }),
    ], limits());
    expect(result).toHaveLength(2);
    expect(result.map(entry => entry.description)).toEqual(['A storm rolls in.', 'The keeper vanishes.']);
  });

  it('throws when a proposal description exceeds the configured limit', () => {
    expect(() => resolveInitialPlannedEvents([proposal({ description: 'x'.repeat(1001) })], limits({ maxDescriptionCharacters: 1000 })))
      .toThrow(/description exceeds the configured limit/i);
  });

  it('throws when a proposal condition exceeds the configured limit', () => {
    expect(() => resolveInitialPlannedEvents([proposal({ condition: 'x'.repeat(501) })], limits({ maxConditionCharacters: 500 })))
      .toThrow(/condition exceeds the configured limit/i);
  });

  describe('condition normalization', () => {
    it('normalizes a null condition to null', () => {
      const result = resolveInitialPlannedEvents([proposal({ condition: null })], limits());
      expect(result[0].condition).toBeNull();
    });

    it('normalizes an empty condition to null', () => {
      const result = resolveInitialPlannedEvents([proposal({ condition: '' })], limits());
      expect(result[0].condition).toBeNull();
    });

    it('normalizes a whitespace-only condition to null', () => {
      const result = resolveInitialPlannedEvents([proposal({ condition: '   ' })], limits());
      expect(result[0].condition).toBeNull();
    });

    it('trims a condition', () => {
      const result = resolveInitialPlannedEvents([proposal({ condition: '  the storm must pass  ' })], limits());
      expect(result[0].condition).toBe('the storm must pass');
    });
  });
});

describe('cullPlannedEventsToLimits', () => {
  it('returns every entry unchanged and reports nothing removed when already within limits', () => {
    const entries = [event({ id: 'a' }), event({ id: 'b' })];
    const result = cullPlannedEventsToLimits(entries, limits());
    expect(result.entries.map(x => x.id).sort()).toEqual(['a', 'b']);
    expect(result.removed).toEqual([]);
  });

  it('reports the same entries applyPlannedEvents would evict for an entry-count overflow', () => {
    const original = [
      event({ id: 'low', importance: 1, lastRelevantTurnNumber: 5 }),
      event({ id: 'high-old', importance: 4, lastRelevantTurnNumber: 1 }),
      event({ id: 'high-new', importance: 4, lastRelevantTurnNumber: 9 }),
    ];
    const result = cullPlannedEventsToLimits(original, limits({ maxEntries: 2 }));
    expect(result.entries.map(x => x.id)).toEqual(['high-new', 'high-old']);
    expect(result.removed.map(x => x.id)).toEqual(['low']);
  });

  it('never removes a mandatory entry even under a tight entry-count limit', () => {
    const mandatory = event({ id: 'mandatory', importance: MANDATORY_IMPORTANCE });
    const other = event({ id: 'other', importance: 1 });
    const result = cullPlannedEventsToLimits([mandatory, other], limits({ maxEntries: 1 }));
    expect(result.entries.map(x => x.id)).toEqual(['mandatory']);
    expect(result.removed.map(x => x.id)).toEqual(['other']);
  });

  it('reports an oversized entry as removed', () => {
    const oversized = event({ id: 'huge', condition: 'x'.repeat(4000) });
    const fine = event({ id: 'fine' });
    const result = cullPlannedEventsToLimits([oversized, fine], limits({ maxEntryCharacters: 500, maxConditionCharacters: 4000 }));
    expect(result.entries.map(x => x.id)).toEqual(['fine']);
    expect(result.removed.map(x => x.id)).toEqual(['huge']);
  });

  it('does not mutate the input array', () => {
    const original = [event({ id: 'a' }), event({ id: 'b' })];
    cullPlannedEventsToLimits(original, limits({ maxEntries: 1 }));
    expect(original).toHaveLength(2);
  });
});

describe('plannedEventCapacity', () => {
  it('reports remaining room and usedPercent', () => {
    const capacity = plannedEventCapacity([event(), event({ id: 'e2' })], 10, 80);
    expect(capacity).toEqual({ count: 2, max: 10, remaining: 8, usedPercent: 20, warningPercent: 80 });
  });

  it('clamps remaining at zero when already over the limit', () => {
    const capacity = plannedEventCapacity([event(), event({ id: 'e2' })], 1, 80);
    expect(capacity.remaining).toBe(0);
  });

  it('reports full usage when the limit is zero', () => {
    const capacity = plannedEventCapacity([], 0, 80);
    expect(capacity.usedPercent).toBe(100);
  });
});

describe('isWithinPlannedEventLimits', () => {
  it('is true when every budget is satisfied', () => {
    expect(isWithinPlannedEventLimits([event()], limits())).toBe(true);
  });

  it('is false when the entry count exceeds the maximum', () => {
    expect(isWithinPlannedEventLimits([event({ id: 'a' }), event({ id: 'b' })], limits({ maxEntries: 1 }))).toBe(false);
  });

  it('is false when an entry exceeds the per-entry character budget', () => {
    expect(isWithinPlannedEventLimits([event({ condition: 'x'.repeat(4000) })], limits({ maxEntryCharacters: 500 }))).toBe(false);
  });

  it('is false when total serialized size exceeds the total character budget', () => {
    const values = [event({ id: 'a' }), event({ id: 'b' })];
    expect(isWithinPlannedEventLimits(values, limits({ maxTotalCharacters: JSON.stringify(values).length - 1 }))).toBe(false);
  });
});

describe('isApproachingPlannedEventLimits', () => {
  it('is false when well under every limit', () => {
    expect(isApproachingPlannedEventLimits([event()], limits(), 80)).toBe(false);
  });

  it('is true when entry count is at or above the warning threshold', () => {
    const values = [event({ id: 'a' }), event({ id: 'b' })];
    expect(isApproachingPlannedEventLimits(values, limits({ maxEntries: 2 }), 80)).toBe(true);
  });

  it('is true when the largest entry is at or above the warning threshold of the per-entry budget', () => {
    const values = [event({ condition: 'x'.repeat(400) })];
    const entryCharacters = JSON.stringify(values[0]).length;
    // floor (not ceil) so entryCharacters / maxEntryCharacters is guaranteed >= 0.8, avoiding an
    // off-by-rounding false negative right at the threshold.
    expect(isApproachingPlannedEventLimits(values, limits({ maxEntryCharacters: Math.floor(entryCharacters / 0.8) }), 80)).toBe(true);
  });

  it('is true when total serialized size is at or above the warning threshold of the total budget', () => {
    const values = [event({ id: 'a' }), event({ id: 'b' })];
    const totalCharacters = JSON.stringify(values).length;
    expect(isApproachingPlannedEventLimits(values, limits({ maxTotalCharacters: Math.floor(totalCharacters / 0.8) }), 80)).toBe(true);
  });

  it('reports nothing approaching when there are no entries', () => {
    expect(isApproachingPlannedEventLimits([], limits(), 80)).toBe(false);
  });
});
