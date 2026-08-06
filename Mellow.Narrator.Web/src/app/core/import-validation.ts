import { AppSettings, StoryDefinition, StoryState } from './models';

// Mirrors Mellow.Narrator.Core's ImportExportProcessor.ValidateDefinition/ValidateState as closely as
// sensible for what Angular's file-based import actually needs: reject a malformed or hand-edited
// import file with a clear message instead of letting `undefined`/missing fields crash downstream code
// (Story Bible/Planned Event rendering, turn ordering, etc.) after it's already been saved. Not every
// C# check has an Angular equivalent (there's no on-disk maintenance-history/limit-snapshot concept
// here), but the structural checks that guard against a genuinely broken import are covered: required
// fields present and correctly typed, non-empty title/label within configured limits, unique non-empty
// IDs across every Story Bible/Planned Event/Condition list, condition-reference IDs that actually
// exist, and (for a Story State) a contiguous turn sequence starting at 0 with consistent turn identity.
// See Mellow.Narrator.Core/ImportExportProcessor.cs for the source of truth this mirrors.

type Limits = Pick<AppSettings,
  'maxStoryTitleCharacters' | 'maxStoryPromptCharacters' | 'maxStoryLabelCharacters' |
  'maxNarrationCharacters' | 'maxPlayerActionCharacters' | 'maxSuggestedActionCharacters' | 'maxSuggestedActions'>;

function isNonEmptyString(value: unknown): value is string {
  return typeof value === 'string' && value.trim().length > 0;
}

function textWithinLimit(value: unknown, maxLength: number): boolean {
  return isNonEmptyString(value) && value.length <= maxLength;
}

function optionalTextWithinLimit(value: unknown, maxLength: number): boolean {
  return value === null || value === undefined || (typeof value === 'string' && value.length <= maxLength);
}

function hasUniqueEntries(values: unknown): boolean {
  return Array.isArray(values) && new Set(values).size === values.length;
}

function uniqueNonEmptyIds(entries: unknown): boolean {
  if (!Array.isArray(entries)) return false;
  const ids = entries.map(x => x?.id);
  return ids.every(isNonEmptyString) && new Set(ids).size === ids.length;
}

function validateBible(entries: unknown): string | null {
  if (!Array.isArray(entries)) return 'The Story Bible is missing or malformed.';
  if (!uniqueNonEmptyIds(entries)) return 'Story Bible entry IDs are invalid.';
  for (const entry of entries) {
    if (!isNonEmptyString(entry?.category) || !isNonEmptyString(entry?.name)) {
      return 'A Story Bible entry is missing its category or name.';
    }
    if (!Array.isArray(entry?.knownFacts) || !Array.isArray(entry?.secretFacts)) {
      return 'A Story Bible entry has malformed facts.';
    }
  }
  return null;
}

function validatePlannedEvents(entries: unknown): string | null {
  if (!Array.isArray(entries)) return 'The Planned Events are missing or malformed.';
  if (!uniqueNonEmptyIds(entries)) return 'Planned Event IDs are invalid.';
  for (const entry of entries) {
    if (!isNonEmptyString(entry?.description)) return 'A Planned Event is missing its description.';
    if (!Number.isFinite(entry?.importance) || entry.importance < 1 || entry.importance > 5) {
      return 'A Planned Event has an invalid importance.';
    }
    if (!Number.isFinite(entry?.urgency) || entry.urgency < 1 || entry.urgency > 5) {
      return 'A Planned Event has an invalid urgency.';
    }
  }
  return null;
}

function validateConditions(entries: unknown, name: string): string | null {
  if (!Array.isArray(entries)) return `The ${name} are missing or malformed.`;
  if (!uniqueNonEmptyIds(entries)) return `${name} IDs are invalid.`;
  for (const entry of entries) {
    if (!isNonEmptyString(entry?.description)) return `A ${name.slice(0, -1)} is missing its description.`;
    if (typeof entry?.secret !== 'boolean') return `A ${name.slice(0, -1)} has an invalid secret flag.`;
  }
  return null;
}

function validateReferencedIds(conditions: unknown, ids: unknown, name: string): string | null {
  if (!Array.isArray(ids)) return `The ${name} IDs are malformed.`;
  if (new Set(ids).size !== ids.length) return `A ${name} ID is invalid.`;
  const known = new Set((Array.isArray(conditions) ? conditions : []).map((c: any) => c?.id));
  if (ids.some((id: unknown) => !known.has(id))) return `A ${name} ID is invalid.`;
  return null;
}

// Shared by both Definition and State import: validates the "initial*" content a Story Definition
// (standalone, or embedded as a Story State's setup snapshot) carries.
function validateDefinitionContent(value: any, limits: Limits, titleLabel: string, promptLabel: string): string | null {
  if (!value || typeof value !== 'object') return 'The Story Definition snapshot is missing.';
  if (!textWithinLimit(value.title, limits.maxStoryTitleCharacters)) return `The ${titleLabel} is empty or too long.`;
  if (!textWithinLimit(value.storyPrompt, limits.maxStoryPromptCharacters)) return `The ${promptLabel} is empty or too long.`;
  if (!optionalTextWithinLimit(value.initialEventsPrompt, limits.maxStoryPromptCharacters)) {
    return 'The Initial Events prompt exceeds its configured limit.';
  }
  return (
    validateBible(value.initialStoryBible) ??
    validatePlannedEvents(value.initialPlannedEvents) ??
    validateConditions(value.initialVictoryConditions, 'Victory Conditions') ??
    validateConditions(value.initialLossConditions, 'Loss Conditions')
  );
}

// Mirrors ImportExportProcessor.ValidateDefinition. Returns an error message, or null if `value` is
// structurally sound enough to save.
export function validateImportedDefinition(value: Partial<StoryDefinition> | null | undefined, limits: Limits): string | null {
  if (!value || typeof value !== 'object') return 'The imported file does not contain a Story Definition.';
  if (!isNonEmptyString(value.id)) return 'The Story Definition ID is invalid.';
  return validateDefinitionContent(value, limits, 'Story Definition title', 'Story Prompt');
}

// Mirrors ImportExportProcessor.ValidateState. Returns an error message, or null if `value`/`value.turns`
// is structurally sound enough to save. Angular's StoryState has no separate LastCommittedTurnSequence
// field the way Mellow.Narrator.Core's does, so the turn-contiguity check below uses turns.length as the
// equivalent bookkeeping value (the highest sequence number must be turns.length - 1).
export function validateImportedState(value: Partial<StoryState> | null | undefined, limits: Limits): string | null {
  if (!value || typeof value !== 'object') return 'The imported file does not contain a Story.';
  if (!isNonEmptyString(value.id)) return 'The Story ID is invalid.';
  if (!textWithinLimit(value.label, limits.maxStoryLabelCharacters)) return 'The Story label is empty or too long.';

  const definitionError = validateDefinitionContent(value.definition, limits, 'snapshot title', 'snapshot Story Prompt');
  if (definitionError) return definitionError;

  const contentError =
    validateBible(value.currentStoryBible) ??
    validatePlannedEvents(value.currentPlannedEvents) ??
    validateConditions(value.currentVictoryConditions, 'Victory Conditions') ??
    validateConditions(value.currentLossConditions, 'Loss Conditions');
  if (contentError) return contentError;

  const referenceError =
    validateReferencedIds(value.currentVictoryConditions, value.revealedVictoryConditionIds, 'revealed Victory Condition') ??
    validateReferencedIds(value.currentVictoryConditions, value.metVictoryConditionIds, 'met Victory Condition') ??
    validateReferencedIds(value.currentLossConditions, value.revealedLossConditionIds, 'revealed Loss Condition') ??
    validateReferencedIds(value.currentLossConditions, value.metLossConditionIds, 'met Loss Condition');
  if (referenceError) return referenceError;

  const turns = value.turns;
  if (!Array.isArray(turns) || turns.length === 0) return 'The Story has no turns.';
  const ordered = [...turns].sort((a: any, b: any) => a.sequenceNumber - b.sequenceNumber);
  if (ordered.some((turn: any, index: number) => turn?.sequenceNumber !== index)) {
    return 'Story turns are not contiguous.';
  }
  const turnIds = ordered.map((turn: any) => turn?.id);
  if (!turnIds.every(isNonEmptyString) || new Set(turnIds).size !== turnIds.length) {
    return 'Story Turn identities are invalid.';
  }
  if (ordered.some((turn: any) => turn?.storyStateId !== value.id)) return 'Story Turn identities are invalid.';
  const opening = ordered[0] as any;
  if (opening.playerAction !== null && opening.playerAction !== undefined) {
    return 'The opening turn must not contain a player action.';
  }

  for (const turn of ordered as any[]) {
    if (!textWithinLimit(turn.narration, limits.maxNarrationCharacters)) return 'A turn narration is empty or too long.';
    if (!optionalTextWithinLimit(turn.playerAction, limits.maxPlayerActionCharacters)) return 'A turn player action is too long.';
    if (!Array.isArray(turn.suggestedActions) || turn.suggestedActions.length > limits.maxSuggestedActions) {
      return 'A turn has too many suggested actions.';
    }
    if (turn.suggestedActions.some((action: unknown) => !textWithinLimit(action, limits.maxSuggestedActionCharacters))) {
      return 'A suggested action is empty or too long.';
    }
    if (!isNonEmptyString(turn.completedAtUtc)) return 'A turn timestamp is invalid.';
    if (!isNonEmptyString(turn.modelId)) return 'A turn is missing its model ID.';
    if (!hasUniqueEntries(turn.relevantStoryBibleEntryIds)) return 'A turn contains duplicate relevant-entry IDs.';
    if (!hasUniqueEntries(turn.relevantPlannedEventIds)) return 'A turn contains duplicate relevant Planned Event IDs.';
    if (!hasUniqueEntries(turn.revealedVictoryConditionIds) || !hasUniqueEntries(turn.metVictoryConditionIds) ||
      !hasUniqueEntries(turn.revealedLossConditionIds) || !hasUniqueEntries(turn.metLossConditionIds)) {
      return 'A turn contains duplicate condition IDs.';
    }
  }

  return null;
}
