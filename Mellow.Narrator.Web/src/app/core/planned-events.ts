import { PlannedEvent, PlannedEventUpdate, ProposedPlannedEvent, uuid } from './models';

// Importance 5 is the maximum and marks a Planned Event mandatory - see the PlannedEvent doc comment
// in models.ts. Kept here as the single source of truth for the threshold.
export const MANDATORY_IMPORTANCE = 5;

export interface PlannedEventLimits {
  maxEntries: number;
  maxEntryCharacters: number;
  maxTotalCharacters: number;
  maxDescriptionCharacters: number;
  maxConditionCharacters: number;
}

/**
 * Applies a turn's proposed Planned Event updates to the current list, enforcing the mandatory-removal
 * rule and culling to the configured maximum (never evicting a mandatory entry by count or total size -
 * see cull()). Throws on a genuine rule violation - missing/out-of-range/oversized fields, an attempt to
 * abandon or demote a mandatory entry, or an update referencing an unknown/already-touched entry -
 * mirroring how this app already throws on other structurally important model mistakes (see
 * LlmService.generateTurn's narration/suggestedActions checks) rather than silently discarding a turn's
 * other valid changes.
 */
export function applyPlannedEvents(
  original: PlannedEvent[],
  updates: PlannedEventUpdate[],
  relevantIds: string[],
  sequence: number,
  limits: PlannedEventLimits,
): PlannedEvent[] {
  const values = structuredClone(original);
  const byId = new Map(values.map(entry => [entry.id, entry]));
  const relevant = new Set(relevantIds);

  const touched = new Set<string>();
  updates.forEach(update => {
    if (update.operation === 'add') {
      validateProposal(update.entry, limits);
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
    validateProposal(update.entry, limits);
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

  return cull(values, limits);
}

/**
 * Resolves a batch of brand-new proposals with no pre-existing entries - the initial Planned Events
 * proposed alongside a Story Definition - into real Planned Events, assigning each an id via uuid.
 */
export function resolveInitialPlannedEvents(
  proposals: ProposedPlannedEvent[],
  limits: Pick<PlannedEventLimits, 'maxDescriptionCharacters' | 'maxConditionCharacters'>,
): PlannedEvent[] {
  proposals.forEach(proposal => validateProposal(proposal, limits));
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

/**
 * True when every configured budget is satisfied: entry count, each individual entry's serialized
 * character size, and the total serialized character size - mirroring PlannedEventProcessor.IsWithinLimits.
 */
export function isWithinPlannedEventLimits(events: PlannedEvent[], limits: PlannedEventLimits): boolean {
  return events.length <= limits.maxEntries
    && events.every(entry => serializedLength(entry) <= limits.maxEntryCharacters)
    && serializedLength(events) <= limits.maxTotalCharacters;
}

/**
 * True when the Planned Events list is within limits but close enough to one of them (count, largest
 * entry size, or total size) to warn the user before it's actually exceeded - mirroring
 * PlannedEventProcessor.IsApproachingLimits. warningPercent is the configured threshold, e.g. 80 for "warn
 * at 80% of any limit".
 */
export function isApproachingPlannedEventLimits(events: PlannedEvent[], limits: PlannedEventLimits, warningPercent: number): boolean {
  const threshold = warningPercent / 100;
  const largest = events.length === 0 ? 0 : Math.max(...events.map(serializedLength));
  return events.length >= limits.maxEntries * threshold
    || largest >= limits.maxEntryCharacters * threshold
    || serializedLength(events) >= limits.maxTotalCharacters * threshold;
}

/**
 * User-triggered "cull to limits" action, exposed for a manual cull of a Story Definition's initial
 * Planned Events or a Story State's current Planned Events - mirroring
 * NarratorApplication.CullDefinitionAsync/CullStoryStateAsync. Reuses the exact eviction rules
 * applyPlannedEvents already applies via cull() (so a manual cull and a turn's incidental cull always
 * agree on what gets removed, including never evicting a mandatory entry), but also reports what got
 * removed so the caller can show the user a preview/confirmation before persisting.
 */
export function cullPlannedEventsToLimits(
  entries: PlannedEvent[],
  limits: PlannedEventLimits,
): { entries: PlannedEvent[]; removed: PlannedEvent[] } {
  const culled = cull(structuredClone(entries), limits);
  const survivingIds = new Set(culled.map(entry => entry.id));
  const removed = entries.filter(entry => !survivingIds.has(entry.id));
  return { entries: culled, removed };
}

function validateProposal(
  entry: ProposedPlannedEvent | null,
  limits: Pick<PlannedEventLimits, 'maxDescriptionCharacters' | 'maxConditionCharacters'>,
): void {
  if (!entry || !entry.description.trim()) throw new Error('A Planned Event is incomplete.');
  if (entry.description.length > limits.maxDescriptionCharacters)
    throw new Error('A Planned Event description exceeds the configured limit.');
  if (entry.importance < 1 || entry.importance > 5) throw new Error('Planned Event importance must be from 1 to 5.');
  if (entry.urgency < 1 || entry.urgency > 5) throw new Error('Planned Event urgency must be from 1 to 5.');
  if (entry.condition && entry.condition.length > limits.maxConditionCharacters)
    throw new Error('A Planned Event condition exceeds the configured limit.');
}

// Empty/whitespace-only becomes null (no prerequisite); otherwise trimmed.
function normalizeCondition(condition: string | null | undefined): string | null {
  return condition && condition.trim() ? condition.trim() : null;
}

// Measures the JSON-serialized length (including property names, quoting, and escaping), not the raw
// character count of the entry's text - mirroring PlannedEventProcessor.SerializedLength, so the usable
// text budget is smaller than the configured number suggests, and shifts if this shape ever changes.
function serializedLength(value: unknown): number {
  return JSON.stringify(value).length;
}

// First evicts any entry - mandatory or not - whose serialized size exceeds the per-entry character
// budget outright, mirroring PlannedEventProcessor.Cull's unconditional first pass. Then repeatedly
// evicts the lowest-importance/least-recently-relevant remaining entry (ties broken by id, for
// determinism) while still over the entry-count limit or the total character budget - but, unlike the
// first pass, a mandatory (importance 5) entry is never a candidate for eviction in this second pass, so
// a count/total overflow made up entirely of mandatory entries survives unresolved and is surfaced as a
// thrown error instead of silently exceeding the configured limit.
function cull(values: PlannedEvent[], limits: PlannedEventLimits): PlannedEvent[] {
  const sized = values.filter(entry => serializedLength(entry) <= limits.maxEntryCharacters);

  const mandatory = sized.filter(entry => entry.importance === MANDATORY_IMPORTANCE);
  const rest = sized
    .filter(entry => entry.importance !== MANDATORY_IMPORTANCE)
    .sort((a, b) => b.importance - a.importance || b.lastRelevantTurnNumber - a.lastRelevantTurnNumber);

  while (
    rest.length > 0
    && (mandatory.length + rest.length > limits.maxEntries
      || serializedLength([...mandatory, ...rest]) > limits.maxTotalCharacters)
  ) {
    rest.pop();
  }

  if (mandatory.length > limits.maxEntries)
    throw new Error('There are too many mandatory Planned Events to fit within the configured limit.');
  if (serializedLength(mandatory) > limits.maxTotalCharacters)
    throw new Error('The mandatory Planned Events exceed the configured total character limit.');

  return [...mandatory, ...rest];
}
