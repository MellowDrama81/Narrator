import { PlannedEvent, PlannedEventUpdate, ProposedPlannedEvent, uuid } from './models';

// Importance 5 is the maximum and marks a Planned Event mandatory - see the PlannedEvent doc comment
// in models.ts. Kept here as the single source of truth for the threshold.
export const MANDATORY_IMPORTANCE = 5;

/**
 * Applies a turn's proposed Planned Event updates to the current list, enforcing the mandatory-removal
 * rule, resolving batch-scoped key references (see ProposedPlannedEvent), validating the result has no
 * self-reference or cycle, and culling to the configured maximum (never evicting a mandatory entry).
 * Throws on a genuine rule violation - missing/out-of-range fields, an unknown prerequisite id or key, a
 * duplicate key, an attempt to abandon or demote a mandatory entry, or a resulting self-reference/cycle -
 * mirroring how this app already throws on other structurally important model mistakes (see
 * LlmService.generateTurn's narration/suggestedActions checks) rather than silently discarding a turn's
 * other valid changes.
 */
export function applyPlannedEvents(
  original: PlannedEvent[],
  updates: PlannedEventUpdate[],
  relevantIds: string[],
  sequence: number,
  maxEntries: number,
): PlannedEvent[] {
  const values = structuredClone(original);
  const byId = new Map(values.map(entry => [entry.id, entry]));
  const knownIds = new Set(values.map(entry => entry.id));
  const relevant = new Set(relevantIds);

  // Assign every Add a real id up front and collect a Key -> id map, so a proposal in this same batch
  // (including another Add) can reference a sibling Add's id via prerequisiteKeys before it's actually
  // created below - order within the batch doesn't matter, and the model never invents a real id.
  const addedIds = new Map<number, string>();
  const keyToId = new Map<string, string>();
  updates.forEach((update, index) => {
    if (update.operation !== 'add' || !update.entry) return;
    const id = uuid();
    addedIds.set(index, id);
    const key = update.entry.key;
    if (key) {
      if (keyToId.has(key)) throw new Error(`Duplicate Planned Event key '${key}' in the same batch.`);
      keyToId.set(key, id);
    }
  });

  const touched = new Set<string>();
  updates.forEach((update, index) => {
    if (update.operation === 'add') {
      validateProposal(update.entry, null, knownIds);
      const id = addedIds.get(index)!;
      const created: PlannedEvent = {
        id,
        description: update.entry!.description.trim(),
        importance: update.entry!.importance,
        urgency: update.entry!.urgency,
        prerequisiteEventIds: resolvePrerequisites(update.entry!, keyToId),
        lastRelevantTurnNumber: sequence,
      };
      values.push(created);
      byId.set(id, created);
      relevant.add(id);
      return;
    }

    const before = update.entryId ? byId.get(update.entryId) : undefined;
    if (!update.entryId || !before) throw new Error('A Planned Event update references an unknown entry.');
    if (touched.has(update.entryId)) throw new Error('A Planned Event was updated more than once.');
    touched.add(update.entryId);

    if (update.operation === 'remove') {
      if (update.entry || relevant.has(before.id))
        throw new Error('A removed Planned Event cannot also be relevant or contain a replacement.');
      if (!update.outcome)
        throw new Error('A Planned Event removal must state whether the event was fulfilled or abandoned.');
      if (before.importance === MANDATORY_IMPORTANCE && update.outcome !== 'fulfilled')
        throw new Error('A mandatory Planned Event can only be removed once it has occurred in the story.');
      const index = values.findIndex(entry => entry.id === before.id);
      values.splice(index, 1);
      byId.delete(before.id);
      return;
    }

    // replace
    validateProposal(update.entry, before, knownIds);
    if (before.importance === MANDATORY_IMPORTANCE && update.entry!.importance !== MANDATORY_IMPORTANCE)
      throw new Error("A mandatory Planned Event's importance cannot be reduced; remove it as fulfilled once it occurs.");
    const replaceIndex = values.findIndex(entry => entry.id === before.id);
    const after: PlannedEvent = {
      id: before.id,
      description: update.entry!.description.trim(),
      importance: update.entry!.importance,
      urgency: update.entry!.urgency,
      prerequisiteEventIds: resolvePrerequisites(update.entry!, keyToId),
      lastRelevantTurnNumber: sequence,
    };
    values[replaceIndex] = after;
    byId.set(before.id, after);
    relevant.add(before.id);
  });

  for (const entry of values) if (relevant.has(entry.id)) entry.lastRelevantTurnNumber = sequence;

  validateRelationships(values);
  return cull(values, maxEntries);
}

/**
 * Resolves a batch of brand-new proposals with no pre-existing entries - the initial Planned Events
 * proposed alongside a Story Definition - into real Planned Events, resolving key/prerequisiteKeys
 * against each other the same way applyPlannedEvents resolves an Add's keys against its batch siblings.
 * Throws on a duplicate key, an unresolvable key, or a resulting self-reference/cycle.
 */
export function resolveInitialPlannedEvents(proposals: ProposedPlannedEvent[]): PlannedEvent[] {
  const ids = proposals.map(() => uuid());
  const keyToId = new Map<string, string>();
  proposals.forEach((proposal, index) => {
    if (!proposal.key) return;
    if (keyToId.has(proposal.key)) throw new Error(`Duplicate Planned Event key '${proposal.key}' in the same batch.`);
    keyToId.set(proposal.key, ids[index]);
  });
  const values = proposals.map((proposal, index) => ({
    id: ids[index],
    description: proposal.description.trim(),
    importance: proposal.importance,
    urgency: proposal.urgency,
    prerequisiteEventIds: resolvePrerequisites(proposal, keyToId),
    lastRelevantTurnNumber: 0,
  }));
  validateRelationships(values);
  return values;
}

export interface PlannedEventCapacity {
  count: number;
  max: number;
  remaining: number;
  usedPercent: number;
  warningPercent: number;
}

/** Reported to the model with every request so it can scale its own eagerness to propose new Planned
 * Events against remaining room - see the Capacity section of story-narration.md. */
export function plannedEventCapacity(current: PlannedEvent[], maxEntries: number, warningPercent: number): PlannedEventCapacity {
  const count = current.length;
  return {
    count,
    max: maxEntries,
    remaining: Math.max(0, maxEntries - count),
    usedPercent: maxEntries > 0 ? Math.round((100 * count) / maxEntries) : 100,
    warningPercent,
  };
}

// A newly introduced prerequisite id (relative to before.prerequisiteEventIds for a replace, or all of
// them for an add) must be one the model actually saw this turn (knownIds), to catch a
// hallucinated/invented reference - an id the entry already carried is always allowed to be echoed back
// or dropped, since a replace resends the full entry and that id is the entry's own history.
function validateProposal(entry: ProposedPlannedEvent | null, before: PlannedEvent | null, knownIds: Set<string>): void {
  if (!entry || !entry.description.trim()) throw new Error('A Planned Event is incomplete.');
  if (entry.importance < 1 || entry.importance > 5) throw new Error('Planned Event importance must be from 1 to 5.');
  if (entry.urgency < 1 || entry.urgency > 5) throw new Error('Planned Event urgency must be from 1 to 5.');
  const existing = new Set(before?.prerequisiteEventIds ?? []);
  const newPrerequisites = entry.prerequisiteEventIds.filter(id => !existing.has(id));
  if (newPrerequisites.some(id => !knownIds.has(id)))
    throw new Error('A Planned Event lists an unknown prerequisite event.');
}

// Merges prerequisiteEventIds (real ids, already checked against knownIds by validateProposal) with
// prerequisiteKeys resolved through the batch's key -> id map. An unresolvable key means the model
// referenced a label no proposal in this batch actually declared.
function resolvePrerequisites(entry: ProposedPlannedEvent, keyToId: Map<string, string>): string[] {
  const resolved = new Set(entry.prerequisiteEventIds);
  for (const key of entry.prerequisiteKeys) {
    const id = keyToId.get(key);
    if (!id) throw new Error(`A Planned Event references an unknown prerequisite key '${key}'.`);
    resolved.add(id);
  }
  return [...resolved];
}

// A prerequisite id that no longer names a live entry is never an error - see the PlannedEvent doc
// comment in models.ts - so only edges pointing at a still-live entry participate in cycle detection.
function validateRelationships(values: PlannedEvent[]): void {
  for (const entry of values)
    if (entry.prerequisiteEventIds.includes(entry.id))
      throw new Error('A Planned Event cannot list itself as a prerequisite.');

  const byId = new Map(values.map(entry => [entry.id, entry]));
  const state = new Map<string, 1 | 2>(); // 1 = on the current path (cycle if revisited), 2 = fully explored
  const visit = (id: string): boolean => {
    const visited = state.get(id);
    if (visited !== undefined) return visited === 1;
    state.set(id, 1);
    const entry = byId.get(id);
    if (entry) for (const prerequisiteId of entry.prerequisiteEventIds) if (byId.has(prerequisiteId) && visit(prerequisiteId)) return true;
    state.set(id, 2);
    return false;
  };
  for (const id of byId.keys()) if (visit(id)) throw new Error('Planned Events contain a prerequisite cycle.');
}

// Sorted by importance then recency, same as applyBible's cull - but a mandatory (importance 5) entry
// is never a candidate for eviction, so it's excluded from the pool considered for the cut rather than
// just sorted first, in case there are more mandatory entries than maxEntries allows.
function cull(values: PlannedEvent[], maxEntries: number): PlannedEvent[] {
  const mandatory = values.filter(entry => entry.importance === MANDATORY_IMPORTANCE);
  if (mandatory.length > maxEntries)
    throw new Error('There are too many mandatory Planned Events to fit within the configured limit.');
  const rest = values
    .filter(entry => entry.importance !== MANDATORY_IMPORTANCE)
    .sort((a, b) => b.importance - a.importance || b.lastRelevantTurnNumber - a.lastRelevantTurnNumber)
    .slice(0, maxEntries - mandatory.length);
  return [...mandatory, ...rest];
}
