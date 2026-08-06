import {
  applyPlannedEvents, MANDATORY_IMPORTANCE, plannedEventCapacity, resolveInitialPlannedEvents,
} from './planned-events';
import { PlannedEvent, PlannedEventUpdate, ProposedPlannedEvent } from './models';

const proposal = (overrides: Partial<ProposedPlannedEvent> = {}): ProposedPlannedEvent => ({
  description: 'The lighthouse keeper vanishes.',
  importance: 3,
  urgency: 3,
  prerequisiteEventIds: [],
  key: null,
  prerequisiteKeys: [],
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
  prerequisiteEventIds: [],
  lastRelevantTurnNumber: 0,
  ...overrides,
});

describe('applyPlannedEvents', () => {
  it('adds a new event and marks it relevant', () => {
    const result = applyPlannedEvents([], [add()], [], 1, 50);
    expect(result).toHaveLength(1);
    expect(result[0].description).toBe('The lighthouse keeper vanishes.');
    expect(result[0].lastRelevantTurnNumber).toBe(1);
  });

  it('resolves prerequisiteKeys against a sibling Add in the same batch', () => {
    const updates: PlannedEventUpdate[] = [
      add({ key: 'storm', description: 'A storm rolls in.' }),
      add({ description: 'The keeper vanishes.', prerequisiteKeys: ['storm'] }),
    ];
    const result = applyPlannedEvents([], updates, [], 1, 50);
    const storm = result.find(entry => entry.description === 'A storm rolls in.')!;
    const keeper = result.find(entry => entry.description === 'The keeper vanishes.')!;
    expect(keeper.prerequisiteEventIds).toEqual([storm.id]);
  });

  it('throws on a duplicate key within the same batch', () => {
    const updates: PlannedEventUpdate[] = [add({ key: 'storm' }), add({ key: 'storm' })];
    expect(() => applyPlannedEvents([], updates, [], 1, 50)).toThrow(/duplicate/i);
  });

  it('throws when a prerequisiteKeys reference does not resolve', () => {
    const updates: PlannedEventUpdate[] = [add({ prerequisiteKeys: ['missing'] })];
    expect(() => applyPlannedEvents([], updates, [], 1, 50)).toThrow(/unknown prerequisite key/i);
  });

  it('throws when a replace lists an unknown prerequisite id', () => {
    const original = [event()];
    const updates: PlannedEventUpdate[] = [{
      operation: 'replace', entryId: 'event-1', outcome: null,
      entry: proposal({ prerequisiteEventIds: ['nonexistent'] }),
    }];
    expect(() => applyPlannedEvents(original, updates, [], 1, 50)).toThrow(/unknown prerequisite/i);
  });

  it('allows a replace to keep an existing prerequisite id even if not otherwise known this turn', () => {
    const prerequisite = event({ id: 'prereq-1' });
    const dependent = event({ id: 'event-1', prerequisiteEventIds: ['prereq-1'] });
    const updates: PlannedEventUpdate[] = [{
      operation: 'replace', entryId: 'event-1', outcome: null,
      entry: proposal({ prerequisiteEventIds: ['prereq-1'], description: 'Updated.' }),
    }];
    const result = applyPlannedEvents([prerequisite, dependent], updates, [], 1, 50);
    expect(result.find(entry => entry.id === 'event-1')!.prerequisiteEventIds).toEqual(['prereq-1']);
  });

  it('removes an event when fulfilled', () => {
    const original = [event()];
    const updates: PlannedEventUpdate[] = [{ operation: 'remove', entryId: 'event-1', outcome: 'fulfilled', entry: null }];
    expect(applyPlannedEvents(original, updates, [], 1, 50)).toHaveLength(0);
  });

  it('throws when removing without an outcome', () => {
    const original = [event()];
    const updates: PlannedEventUpdate[] = [{ operation: 'remove', entryId: 'event-1', outcome: null, entry: null }];
    expect(() => applyPlannedEvents(original, updates, [], 1, 50)).toThrow(/fulfilled or abandoned/i);
  });

  it('throws when removing an event that is also marked relevant', () => {
    const original = [event()];
    const updates: PlannedEventUpdate[] = [{ operation: 'remove', entryId: 'event-1', outcome: 'fulfilled', entry: null }];
    expect(() => applyPlannedEvents(original, updates, ['event-1'], 1, 50)).toThrow(/cannot also be relevant/i);
  });

  it('throws when updating the same entry twice in one batch', () => {
    const original = [event()];
    const updates: PlannedEventUpdate[] = [
      { operation: 'replace', entryId: 'event-1', outcome: null, entry: proposal() },
      { operation: 'remove', entryId: 'event-1', outcome: 'fulfilled', entry: null },
    ];
    expect(() => applyPlannedEvents(original, updates, [], 1, 50)).toThrow(/more than once/i);
  });

  it('throws when an update references an unknown entryId', () => {
    const updates: PlannedEventUpdate[] = [{ operation: 'remove', entryId: 'missing', outcome: 'fulfilled', entry: null }];
    expect(() => applyPlannedEvents([], updates, [], 1, 50)).toThrow(/unknown entry/i);
  });

  describe('mandatory events (importance 5)', () => {
    it('rejects abandoning a mandatory event', () => {
      const original = [event({ importance: MANDATORY_IMPORTANCE })];
      const updates: PlannedEventUpdate[] = [{ operation: 'remove', entryId: 'event-1', outcome: 'abandoned', entry: null }];
      expect(() => applyPlannedEvents(original, updates, [], 1, 50)).toThrow(/mandatory/i);
    });

    it('allows fulfilling a mandatory event', () => {
      const original = [event({ importance: MANDATORY_IMPORTANCE })];
      const updates: PlannedEventUpdate[] = [{ operation: 'remove', entryId: 'event-1', outcome: 'fulfilled', entry: null }];
      expect(applyPlannedEvents(original, updates, [], 1, 50)).toHaveLength(0);
    });

    it('rejects demoting a mandatory event via replace', () => {
      const original = [event({ importance: MANDATORY_IMPORTANCE })];
      const updates: PlannedEventUpdate[] = [{
        operation: 'replace', entryId: 'event-1', outcome: null, entry: proposal({ importance: 4 }),
      }];
      expect(() => applyPlannedEvents(original, updates, [], 1, 50)).toThrow(/cannot be reduced/i);
    });

    it('never culls a mandatory event even under a tight limit', () => {
      const original = [event({ importance: MANDATORY_IMPORTANCE })];
      const result = applyPlannedEvents(original, [], [], 1, 1);
      expect(result).toHaveLength(1);
    });

    it('throws when more mandatory events exist than the configured limit', () => {
      const original = [event({ id: 'a', importance: MANDATORY_IMPORTANCE }), event({ id: 'b', importance: MANDATORY_IMPORTANCE })];
      expect(() => applyPlannedEvents(original, [], [], 1, 1)).toThrow(/too many mandatory/i);
    });
  });

  describe('relationship validation', () => {
    it('rejects an event that lists itself as a prerequisite', () => {
      const updates: PlannedEventUpdate[] = [add({ key: 'self', prerequisiteKeys: ['self'] })];
      expect(() => applyPlannedEvents([], updates, [], 1, 50)).toThrow(/itself as a prerequisite/i);
    });

    it('rejects a two-event prerequisite cycle', () => {
      const updates: PlannedEventUpdate[] = [
        add({ key: 'a', prerequisiteKeys: ['b'] }),
        add({ key: 'b', prerequisiteKeys: ['a'] }),
      ];
      expect(() => applyPlannedEvents([], updates, [], 1, 50)).toThrow(/cycle/i);
    });

    it('treats a prerequisite id that no longer names a live entry as resolved, not an error', () => {
      const original = [event({ id: 'event-1', prerequisiteEventIds: ['already-gone'] })];
      const result = applyPlannedEvents(original, [], [], 1, 50);
      expect(result).toHaveLength(1);
      expect(result[0].prerequisiteEventIds).toEqual(['already-gone']);
    });
  });

  describe('culling to the configured maximum', () => {
    it('keeps the highest-importance, most-recently-relevant events first', () => {
      const original = [
        event({ id: 'low', importance: 1, lastRelevantTurnNumber: 5 }),
        event({ id: 'high-old', importance: 4, lastRelevantTurnNumber: 1 }),
        event({ id: 'high-new', importance: 4, lastRelevantTurnNumber: 9 }),
      ];
      const result = applyPlannedEvents(original, [], [], 10, 2);
      expect(result.map(entry => entry.id)).toEqual(['high-new', 'high-old']);
    });
  });
});

describe('resolveInitialPlannedEvents', () => {
  it('resolves keys and prerequisiteKeys across the whole batch', () => {
    const result = resolveInitialPlannedEvents([
      proposal({ key: 'storm', description: 'A storm rolls in.' }),
      proposal({ description: 'The keeper vanishes.', prerequisiteKeys: ['storm'] }),
    ]);
    const storm = result.find(entry => entry.description === 'A storm rolls in.')!;
    const keeper = result.find(entry => entry.description === 'The keeper vanishes.')!;
    expect(keeper.prerequisiteEventIds).toEqual([storm.id]);
  });

  it('throws on a duplicate key', () => {
    expect(() => resolveInitialPlannedEvents([proposal({ key: 'a' }), proposal({ key: 'a' })])).toThrow(/duplicate/i);
  });

  it('throws on a cycle across the initial batch', () => {
    expect(() => resolveInitialPlannedEvents([
      proposal({ key: 'a', prerequisiteKeys: ['b'] }),
      proposal({ key: 'b', prerequisiteKeys: ['a'] }),
    ])).toThrow(/cycle/i);
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
