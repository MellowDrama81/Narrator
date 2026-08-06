import { ProposedStoryCondition, StoryCondition, uuid } from './models';

export interface ConditionLimits {
  maxConditions: number;
  maxDescriptionCharacters: number;
}

/**
 * Resolves a batch of brand-new proposals - the initial victory/loss conditions proposed alongside a
 * Story Definition - into real Story Conditions, assigning each an id via uuid(). Unlike Planned Events,
 * no key/prerequisite resolution is needed here: conditions never reference each other. Throws on an
 * empty or oversized description, mirroring resolveInitialPlannedEvents's error-throwing style for a
 * malformed batch and StoryConditionProcessor.ValidateEntry's length check.
 */
export function resolveInitialConditions(
  proposals: ProposedStoryCondition[],
  limits: Pick<ConditionLimits, 'maxDescriptionCharacters'>,
): StoryCondition[] {
  for (const proposal of proposals) {
    if (!proposal.description.trim()) throw new Error('A condition description is empty.');
    if (proposal.description.length > limits.maxDescriptionCharacters)
      throw new Error('A condition description exceeds the configured limit.');
  }
  return proposals.map(proposal => ({
    id: uuid(),
    description: proposal.description.trim(),
    secret: proposal.secret,
  }));
}

/**
 * True when the condition list is within both configured budgets: no more than maxConditions entries,
 * and no entry's description longer than maxDescriptionCharacters - mirroring
 * StoryConditionProcessor.IsWithinLimits. Conditions have no cull mechanism (the list is fixed once
 * resolved), so this is purely a check, used to validate a hand-authored or imported batch rather than to
 * drive any automatic trimming.
 */
export function isWithinConditionLimits(conditions: StoryCondition[], limits: ConditionLimits): boolean {
  return conditions.length <= limits.maxConditions
    && conditions.every(entry => entry.description.length <= limits.maxDescriptionCharacters);
}

/**
 * Validates and returns this turn's newly revealed/met ids for one condition list (victory or loss).
 * alreadyRevealedIds/alreadyMetIds are the running totals from before this turn; proposedRevealedIds/
 * proposedMetIds are exactly what the model reported this turn. Throws on any rule violation: an unknown
 * id, a duplicate mention (within this turn or against the running totals), or revealing a secret
 * condition - secret ones may only ever be reported met, never revealed. In practice the caller
 * (LlmService.generateTurn, via normalizeConditionIds) has already filtered the model's raw response
 * leniently before this is called, so a genuine throw here means the caller skipped that normalization.
 */
export function applyConditionTurn(
  conditions: StoryCondition[],
  alreadyRevealedIds: string[],
  alreadyMetIds: string[],
  proposedRevealedIds: string[],
  proposedMetIds: string[],
): { revealed: string[]; met: string[] } {
  const byId = new Map(conditions.map(entry => [entry.id, entry]));
  const alreadyRevealed = new Set(alreadyRevealedIds);
  const alreadyMet = new Set(alreadyMetIds);

  const revealed: string[] = [];
  const seenRevealed = new Set<string>();
  for (const id of proposedRevealedIds) {
    const entry = byId.get(id);
    if (!entry) throw new Error('An unknown condition was marked revealed.');
    if (entry.secret) throw new Error('A secret condition cannot be marked revealed.');
    if (seenRevealed.has(id) || alreadyRevealed.has(id)) throw new Error('A condition was marked revealed more than once.');
    seenRevealed.add(id);
    revealed.push(id);
  }

  const met: string[] = [];
  const seenMet = new Set<string>();
  for (const id of proposedMetIds) {
    if (!byId.has(id)) throw new Error('An unknown condition was marked met.');
    if (seenMet.has(id) || alreadyMet.has(id)) throw new Error('A condition was marked met more than once.');
    seenMet.add(id);
    met.push(id);
  }

  return { revealed, met };
}

/**
 * Builds the wire-format payload sent to the model for one condition list (victory or loss). An
 * already-met condition is excluded entirely - nothing left to evaluate for it - while the rest are sent
 * with a revealed flag so the model never re-reveals a non-secret condition already established in the
 * narration.
 */
export function conditionPayload(
  conditions: StoryCondition[],
  revealedIds: string[],
  metIds: string[],
): Array<{ id: string; description: string; secret: boolean; revealed: boolean }> {
  const met = new Set(metIds);
  const revealed = new Set(revealedIds);
  return conditions
    .filter(entry => !met.has(entry.id))
    .map(entry => ({ id: entry.id, description: entry.description, secret: entry.secret, revealed: revealed.has(entry.id) }));
}

/**
 * Leniently filters a raw array of ids (parsed from the model's JSON response, so possibly containing
 * unknown values, duplicates, or a bad type) down to only the ids that are valid candidates for one of
 * revealedVictoryConditionIds/metVictoryConditionIds/revealedLossConditionIds/metLossConditionIds. Unlike
 * applyConditionTurn, this never throws: an invalid id here is a low-stakes narrative-pacing slip, not
 * data corruption, so it's silently dropped - mirroring NormalizeConditionIds/FilterKnownIds in the C#
 * OpenAiCompatibleProvider. Call once with excludeSecret=true for a revealed-ids list (excluding secret
 * conditions and anything already revealed or already met) and once with excludeSecret=false for a
 * met-ids list (excluding only anything already met - a secret condition can still be met).
 */
export function normalizeConditionIds(
  ids: unknown[],
  conditions: StoryCondition[],
  alreadyRevealedIds: string[],
  alreadyMetIds: string[],
  excludeSecret: boolean,
): string[] {
  const alreadyRevealed = new Set(alreadyRevealedIds);
  const alreadyMet = new Set(alreadyMetIds);
  const candidates = new Set(
    conditions
      .filter(entry => !alreadyMet.has(entry.id) && (!excludeSecret || (!entry.secret && !alreadyRevealed.has(entry.id))))
      .map(entry => entry.id),
  );

  const seen = new Set<string>();
  const result: string[] = [];
  for (const raw of ids) {
    if (typeof raw !== 'string') continue;
    if (!candidates.has(raw)) continue;
    if (seen.has(raw)) continue;
    seen.add(raw);
    result.push(raw);
  }
  return result;
}
