import { StoryBibleEntry, StoryBibleUpdate, uuid } from './models';

export interface StoryBibleLimits {
  maxEntries: number;
  maxEntryCharacters: number;
  maxTotalCharacters: number;
}

// The fields a proposed or manually-edited Story Bible entry needs for validation - deliberately a
// structural subset of StoryBibleEntry so both a full entry (manual edit) and a proposed entry (LLM
// add/replace, which has no id/lastRelevantTurnNumber yet) can be validated with the same function.
export type BibleEntryFields = Pick<StoryBibleEntry, 'category' | 'name' | 'knownFacts' | 'secretFacts' | 'importance'>;

/**
 * Field-level validation for a Story Bible entry, ported from StoryBibleProcessor.ValidateEntry: category
 * and name are required, at least one of knownFacts/secretFacts must be non-empty, no individual fact may
 * be empty, and importance must be from 1 to 5. Returns null when valid, or a message describing the
 * first violation found. Used both to gate manual edits (definition-editor/play components, which show
 * the message and abort the save) and internally by applyStoryBible to validate a turn's proposed
 * add/replace entries (which throws instead).
 */
export function validateBibleEntry(entry: BibleEntryFields): string | null {
  if (!entry.category.trim()) return 'A Story Bible category is empty.';
  if (!entry.name.trim()) return 'A Story Bible entry name is empty.';
  if (entry.knownFacts.length === 0 && entry.secretFacts.length === 0)
    return 'A Story Bible entry must have at least one known or secret fact.';
  if (entry.knownFacts.some(fact => !fact.trim()) || entry.secretFacts.some(fact => !fact.trim()))
    return 'A Story Bible entry has an empty fact.';
  if (entry.importance < 1 || entry.importance > 5) return 'Story Bible importance must be from 1 to 5.';
  return null;
}

/**
 * Applies a turn's proposed Story Bible updates to the current list, stamping lastRelevantTurnNumber for
 * every entry the turn marked relevant (including ones just added/replaced), then culling to the
 * configured limits. Throws on a genuine rule violation - an invalid add/replace entry, a relevant id that
 * doesn't exist, an update referencing an unknown/already-touched entry, or a remove that also claims
 * relevance or carries a replacement entry - mirroring how this app already throws on other structurally
 * important model mistakes (see applyPlannedEvents/applyConditionTurn) rather than silently discarding a
 * turn's other valid changes.
 */
export function applyStoryBible(
  original: StoryBibleEntry[],
  updates: StoryBibleUpdate[],
  relevantIds: string[],
  sequence: number,
  limits: StoryBibleLimits,
): StoryBibleEntry[] {
  const values = structuredClone(original);
  const byId = new Map(values.map(entry => [entry.id, entry]));
  const relevant = new Set(relevantIds);
  if ([...relevant].some(id => !byId.has(id)))
    throw new Error('The model marked an unknown Story Bible entry as relevant.');

  const touched = new Set<string>();
  updates.forEach(update => {
    if (update.operation === 'add') {
      validateProposal(update.entry);
      const id = uuid();
      const created = toEntry(id, update.entry!, sequence);
      values.push(created);
      byId.set(id, created);
      relevant.add(id);
      return;
    }

    const before = update.entryId ? byId.get(update.entryId) : undefined;
    if (!update.entryId || !before) throw new Error('A Story Bible update references an unknown entry.');
    if (touched.has(update.entryId)) throw new Error('A Story Bible entry was updated more than once.');
    touched.add(update.entryId);

    if (update.operation === 'remove') {
      if (update.entry || relevant.has(before.id))
        throw new Error('A removed Story Bible entry cannot also be relevant or contain a replacement.');
      const index = values.findIndex(entry => entry.id === before.id);
      values.splice(index, 1);
      byId.delete(before.id);
      return;
    }

    // replace
    validateProposal(update.entry);
    const index = values.findIndex(entry => entry.id === before.id);
    const after = toEntry(before.id, update.entry!, sequence);
    values[index] = after;
    byId.set(before.id, after);
    relevant.add(before.id);
  });

  for (const entry of values) if (relevant.has(entry.id)) entry.lastRelevantTurnNumber = sequence;

  return cull(values, limits);
}

/**
 * True when every configured budget is satisfied: entry count, each individual entry's serialized
 * character size, and the total serialized character size - mirroring StoryBibleProcessor.IsWithinLimits.
 */
export function isWithinBibleLimits(entries: StoryBibleEntry[], limits: StoryBibleLimits): boolean {
  return entries.length <= limits.maxEntries
    && entries.every(entry => serializedLength(entry) <= limits.maxEntryCharacters)
    && serializedLength(entries) <= limits.maxTotalCharacters;
}

/**
 * True when the bible is within limits but close enough to one of them (count, largest entry size, or
 * total size) to warn the user before it's actually exceeded - mirroring
 * StoryBibleProcessor.IsApproachingLimits. warningPercent is the configured threshold, e.g. 80 for "warn
 * at 80% of any limit".
 */
export function isApproachingBibleLimits(entries: StoryBibleEntry[], limits: StoryBibleLimits, warningPercent: number): boolean {
  const threshold = warningPercent / 100;
  const largest = entries.length === 0 ? 0 : Math.max(...entries.map(serializedLength));
  return entries.length >= limits.maxEntries * threshold
    || largest >= limits.maxEntryCharacters * threshold
    || serializedLength(entries) >= limits.maxTotalCharacters * threshold;
}

/**
 * User-triggered "cull to limits" action, exposed for a manual cull of a Story Definition's initial
 * Story Bible or a Story State's current Story Bible - mirroring
 * NarratorApplication.CullDefinitionAsync/CullStoryStateAsync. Reuses the exact eviction rules
 * applyStoryBible already applies via cull() (so a manual cull and a turn's incidental cull always agree
 * on what gets removed), but also reports what got removed so the caller can show the user a preview/
 * confirmation before persisting.
 */
export function cullBibleToLimits(
  entries: StoryBibleEntry[],
  limits: StoryBibleLimits,
): { entries: StoryBibleEntry[]; removed: StoryBibleEntry[] } {
  const culled = cull(structuredClone(entries), limits);
  const survivingIds = new Set(culled.map(entry => entry.id));
  const removed = entries.filter(entry => !survivingIds.has(entry.id));
  return { entries: culled, removed };
}

function validateProposal(entry: StoryBibleUpdate['entry']): void {
  if (!entry) throw new Error('A Story Bible entry is incomplete.');
  const message = validateBibleEntry(entry);
  if (message) throw new Error(message);
}

function toEntry(id: string, entry: NonNullable<StoryBibleUpdate['entry']>, turn: number): StoryBibleEntry {
  return {
    id,
    category: entry.category.trim(),
    name: entry.name.trim(),
    knownFacts: entry.knownFacts.map(fact => fact.trim()),
    secretFacts: entry.secretFacts.map(fact => fact.trim()),
    importance: entry.importance,
    lastRelevantTurnNumber: turn,
  };
}

// Measures the JSON-serialized length (including property names, quoting, and escaping), not the raw
// character count of the entry's text - mirroring StoryBibleProcessor.SerializedLength, so the usable text
// budget is smaller than the configured number suggests, and shifts if this shape ever changes.
function serializedLength(value: unknown): number {
  return JSON.stringify(value).length;
}

// First evicts any entry over the per-entry character budget outright, then repeatedly evicts the
// lowest-importance/least-recently-relevant remaining entry while still over the entry-count limit or the
// total character budget - mirroring StoryBibleProcessor.Cull. Sorting by importance/recency descending
// and popping from the end (rather than filtering out a computed candidate) matches the style of
// applyPlannedEvents' cull() and leaves the result deterministically ordered highest-importance first.
function cull(values: StoryBibleEntry[], limits: StoryBibleLimits): StoryBibleEntry[] {
  const sized = values.filter(entry => serializedLength(entry) <= limits.maxEntryCharacters);
  const sorted = sized.sort((a, b) => b.importance - a.importance || b.lastRelevantTurnNumber - a.lastRelevantTurnNumber);
  while (sorted.length > 0 && (sorted.length > limits.maxEntries || serializedLength(sorted) > limits.maxTotalCharacters))
    sorted.pop();
  return sorted;
}
