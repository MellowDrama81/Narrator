import { PlannedEvent, PlannedEventUpdate, ProposedPlannedEvent, uuid } from './models';

// Importance 5 is the maximum and marks a Planned Event mandatory - see the PlannedEvent doc comment
// in models.ts. Kept here as the single source of truth for the threshold.
export const MANDATORY_IMPORTANCE = 5;

/**
 * Applies a turn's proposed Planned Event updates to the current list, enforcing the mandatory-removal
 * rule and culling to the configured maximum (never evicting a mandatory entry). Throws on a genuine
 * rule violation - missing/out-of-range fields, an attempt to abandon or demote a mandatory entry, or an
 * update referencing an unknown/already-touched entry - mirroring how this app already throws on other
 * structurally important model mistakes (see LlmService.generateTurn's narration/suggestedActions
 * checks) rather than silently discarding a turn's other valid changes.
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
  const relevant = new Set(relevantIds);

  const touched = new Set<string>();
  updates.forEach(update => {
    if (update.operation === 'add') {
      validateProposal(update.entry);
      const id = uuid();
      const created: PlannedEvent = {
        id,
        description: update.entry!.description.trim(),
        importance: update.entry!.importance,
        urgency: update.entry!.urgency,
        condition: normalizeCondition(update.entry!.condition),
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
    validateProposal(update.entry);
    if (before.importance === MANDATORY_IMPORTANCE && update.entry!.importance !== MANDATORY_IMPORTANCE)
      throw new Error("A mandatory Planned Event's importance cannot be reduced; remove it as fulfilled once it occurs.");
    const replaceIndex = values.findIndex(entry => entry.id === before.id);
    const after: PlannedEvent = {
      id: before.id,
      description: update.entry!.description.trim(),
      importance: update.entry!.importance,
      urgency: update.entry!.urgency,
      condition: normalizeCondition(update.entry!.condition),
      lastRelevantTurnNumber: sequence,
    };
    values[replaceIndex] = after;
    byId.set(before.id, after);
    relevant.add(before.id);
  });

  for (const entry of values) if (relevant.has(entry.id)) entry.lastRelevantTurnNumber = sequence;

  return cull(values, maxEntries);
}

/**
 * Resolves a batch of brand-new proposals with no pre-existing entries - the initial Planned Events
 * proposed alongside a Story Definition - into real Planned Events, assigning each an id via uuid.
 */
export function resolveInitialPlannedEvents(proposals: ProposedPlannedEvent[]): PlannedEvent[] {
  proposals.forEach(validateProposal);
  return proposals.map(proposal => ({
    id: uuid(),
    description: proposal.description.trim(),
    importance: proposal.importance,
    urgency: proposal.urgency,
    condition: normalizeCondition(proposal.condition),
    lastRelevantTurnNumber: 0,
  }));
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

function validateProposal(entry: ProposedPlannedEvent | null): void {
  if (!entry || !entry.description.trim()) throw new Error('A Planned Event is incomplete.');
  if (entry.importance < 1 || entry.importance > 5) throw new Error('Planned Event importance must be from 1 to 5.');
  if (entry.urgency < 1 || entry.urgency > 5) throw new Error('Planned Event urgency must be from 1 to 5.');
}

// Empty/whitespace-only becomes null (no prerequisite); otherwise trimmed.
function normalizeCondition(condition: string | null | undefined): string | null {
  return condition && condition.trim() ? condition.trim() : null;
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
