import {
  applyConditionTurn, conditionPayload, normalizeConditionIds, resolveInitialConditions,
} from './story-conditions';
import { ProposedStoryCondition, StoryCondition } from './models';

const proposal = (overrides: Partial<ProposedStoryCondition> = {}): ProposedStoryCondition => ({
  description: 'Reach the lighthouse before dawn.',
  secret: false,
  ...overrides,
});

const condition = (overrides: Partial<StoryCondition> = {}): StoryCondition => ({
  id: 'cond-1',
  description: 'Reach the lighthouse before dawn.',
  secret: false,
  ...overrides,
});

describe('resolveInitialConditions', () => {
  it('assigns ids and trims descriptions', () => {
    const result = resolveInitialConditions([proposal(), proposal({ description: '  The keeper dies.  ', secret: true })]);
    expect(result).toHaveLength(2);
    expect(result[0].id).toBeTruthy();
    expect(result[1].id).not.toBe(result[0].id);
    expect(result[1].description).toBe('The keeper dies.');
    expect(result[1].secret).toBe(true);
  });

  it('throws on an empty description', () => {
    expect(() => resolveInitialConditions([proposal({ description: '   ' })])).toThrow(/empty/i);
  });
});

describe('applyConditionTurn', () => {
  it('accepts a newly revealed non-secret condition', () => {
    const result = applyConditionTurn([condition()], [], [], ['cond-1'], []);
    expect(result.revealed).toEqual(['cond-1']);
    expect(result.met).toEqual([]);
  });

  it('accepts a newly met condition, secret or not', () => {
    const result = applyConditionTurn([condition({ secret: true })], [], [], [], ['cond-1']);
    expect(result.met).toEqual(['cond-1']);
  });

  it('throws when an unknown id is marked revealed', () => {
    expect(() => applyConditionTurn([condition()], [], [], ['missing'], [])).toThrow(/unknown condition/i);
  });

  it('throws when an unknown id is marked met', () => {
    expect(() => applyConditionTurn([condition()], [], [], [], ['missing'])).toThrow(/unknown condition/i);
  });

  it('throws when a secret condition is marked revealed', () => {
    expect(() => applyConditionTurn([condition({ secret: true })], [], [], ['cond-1'], [])).toThrow(/secret condition/i);
  });

  it('throws when a condition is marked revealed twice in the same turn', () => {
    expect(() => applyConditionTurn([condition()], [], [], ['cond-1', 'cond-1'], [])).toThrow(/more than once/i);
  });

  it('throws when a condition already revealed in an earlier turn is marked revealed again', () => {
    expect(() => applyConditionTurn([condition()], ['cond-1'], [], ['cond-1'], [])).toThrow(/more than once/i);
  });

  it('throws when a condition is marked met twice in the same turn', () => {
    expect(() => applyConditionTurn([condition()], [], [], [], ['cond-1', 'cond-1'])).toThrow(/more than once/i);
  });

  it('throws when a condition already met in an earlier turn is marked met again', () => {
    expect(() => applyConditionTurn([condition()], [], ['cond-1'], [], ['cond-1'])).toThrow(/more than once/i);
  });

  it('allows a condition to be both revealed and met in the same turn', () => {
    const result = applyConditionTurn([condition()], [], [], ['cond-1'], ['cond-1']);
    expect(result.revealed).toEqual(['cond-1']);
    expect(result.met).toEqual(['cond-1']);
  });
});

describe('conditionPayload', () => {
  it('excludes an already-met condition entirely', () => {
    const payload = conditionPayload([condition(), condition({ id: 'cond-2' })], [], ['cond-1']);
    expect(payload).toHaveLength(1);
    expect(payload[0].id).toBe('cond-2');
  });

  it('marks a revealed condition as revealed', () => {
    const payload = conditionPayload([condition()], ['cond-1'], []);
    expect(payload[0]).toEqual({ id: 'cond-1', description: 'Reach the lighthouse before dawn.', secret: false, revealed: true });
  });

  it('marks an unrevealed condition as not revealed', () => {
    const payload = conditionPayload([condition()], [], []);
    expect(payload[0].revealed).toBe(false);
  });
});

describe('normalizeConditionIds', () => {
  it('drops an unknown id', () => {
    expect(normalizeConditionIds(['missing'], [condition()], [], [], false)).toEqual([]);
  });

  it('drops a non-string entry', () => {
    expect(normalizeConditionIds([42, null, 'cond-1'], [condition()], [], [], false)).toEqual(['cond-1']);
  });

  it('drops a duplicate id', () => {
    expect(normalizeConditionIds(['cond-1', 'cond-1'], [condition()], [], [], false)).toEqual(['cond-1']);
  });

  it('excludes an already-met id regardless of excludeSecret', () => {
    expect(normalizeConditionIds(['cond-1'], [condition()], [], ['cond-1'], false)).toEqual([]);
    expect(normalizeConditionIds(['cond-1'], [condition()], [], ['cond-1'], true)).toEqual([]);
  });

  it('excludes a secret condition when excludeSecret is true, but not when false', () => {
    const secretCondition = [condition({ secret: true })];
    expect(normalizeConditionIds(['cond-1'], secretCondition, [], [], true)).toEqual([]);
    expect(normalizeConditionIds(['cond-1'], secretCondition, [], [], false)).toEqual(['cond-1']);
  });

  it('excludes an already-revealed id only when excludeSecret is true', () => {
    expect(normalizeConditionIds(['cond-1'], [condition()], ['cond-1'], [], true)).toEqual([]);
    expect(normalizeConditionIds(['cond-1'], [condition()], ['cond-1'], [], false)).toEqual(['cond-1']);
  });
});
