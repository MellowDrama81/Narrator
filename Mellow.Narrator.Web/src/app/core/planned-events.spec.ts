import {
  applyPlannedEvents, MANDATORY_IMPORTANCE, plannedEventCapacity, resolveInitialPlannedEvents,
} from './planned-events';
import { PlannedEvent, PlannedEventUpdate, ProposedPlannedEvent } from './models';

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
    const result = applyPlannedEvents([], [add()], [], 1, 50);
    expect(result).toHaveLength(1);
    expect(result[0].description).toBe('The lighthouse keeper vanishes.');
    expect(result[0].lastRelevantTurnNumber).toBe(1);
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

  describe('condition normalization', () => {
    it('normalizes a null condition on add', () => {
      const result = applyPlannedEvents([], [add({ condition: null })], [], 1, 50);
      expect(result[0].condition).toBeNull();
    });

    it('normalizes an empty condition to null on add', () => {
      const result = applyPlannedEvents([], [add({ condition: '' })], [], 1, 50);
      expect(result[0].condition).toBeNull();
    });

    it('normalizes a whitespace-only condition to null on add', () => {
      const result = applyPlannedEvents([], [add({ condition: '   ' })], [], 1, 50);
      expect(result[0].condition).toBeNull();
    });

    it('trims a condition on add', () => {
      const result = applyPlannedEvents([], [add({ condition: '  the storm must pass  ' })], [], 1, 50);
      expect(result[0].condition).toBe('the storm must pass');
    });

    it('normalizes and trims a condition on replace', () => {
      const original = [event({ condition: 'old condition' })];
      const updates: PlannedEventUpdate[] = [{
        operation: 'replace', entryId: 'event-1', outcome: null, entry: proposal({ condition: '  new condition  ' }),
      }];
      const result = applyPlannedEvents(original, updates, [], 1, 50);
      expect(result[0].condition).toBe('new condition');
    });

    it('normalizes an empty condition to null on replace', () => {
      const original = [event({ condition: 'old condition' })];
      const updates: PlannedEventUpdate[] = [{
        operation: 'replace', entryId: 'event-1', outcome: null, entry: proposal({ condition: '' }),
      }];
      const result = applyPlannedEvents(original, updates, [], 1, 50);
      expect(result[0].condition).toBeNull();
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
  it('resolves a batch of proposals into real Planned Events', () => {
    const result = resolveInitialPlannedEvents([
      proposal({ description: 'A storm rolls in.' }),
      proposal({ description: 'The keeper vanishes.' }),
    ]);
    expect(result).toHaveLength(2);
    expect(result.map(entry => entry.description)).toEqual(['A storm rolls in.', 'The keeper vanishes.']);
  });

  describe('condition normalization', () => {
    it('normalizes a null condition to null', () => {
      const result = resolveInitialPlannedEvents([proposal({ condition: null })]);
      expect(result[0].condition).toBeNull();
    });

    it('normalizes an empty condition to null', () => {
      const result = resolveInitialPlannedEvents([proposal({ condition: '' })]);
      expect(result[0].condition).toBeNull();
    });

    it('normalizes a whitespace-only condition to null', () => {
      const result = resolveInitialPlannedEvents([proposal({ condition: '   ' })]);
      expect(result[0].condition).toBeNull();
    });

    it('trims a condition', () => {
      const result = resolveInitialPlannedEvents([proposal({ condition: '  the storm must pass  ' })]);
      expect(result[0].condition).toBe('the storm must pass');
    });
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
