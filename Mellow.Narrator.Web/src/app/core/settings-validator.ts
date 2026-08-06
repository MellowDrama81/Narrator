import { AppSettings } from './models';

// Mirrors Mellow.Narrator.Core's SettingsValidator.Validate as closely as sensible for the fields
// Angular actually has. Range bounds are kept identical to the C# validator so a value accepted (or
// rejected) here would be accepted (or rejected) there too. See Mellow.Narrator.Core/Settings.cs.

// Shared with NarratorApplication's sanity check on LLM-generated initial Story Bible entry counts, so
// the two can't silently diverge - mirrors SettingsValidator.MaxStoryBibleEntriesUpperBound.
const MAX_STORY_BIBLE_ENTRIES_UPPER_BOUND = 2000;
// Mirrors SettingsValidator.MaxPlannedEventsUpperBound.
const MAX_PLANNED_EVENTS_UPPER_BOUND = 500;
// Mirrors SettingsValidator.MaxConditionsUpperBound.
const MAX_CONDITIONS_UPPER_BOUND = 200;

function range(errors: Record<string, string>, name: string, value: number, min: number, max: number): void {
  if (!Number.isFinite(value)) {
    errors[name] = 'Must be a number.';
    return;
  }
  if (value < min || value > max) errors[name] = `Must be between ${min} and ${max}.`;
}

function optionalRange(errors: Record<string, string>, name: string, value: number | null, min: number, max: number): void {
  if (value === null) return;
  range(errors, name, value, min, max);
}

function isAbsoluteHttpUrl(value: string): boolean {
  try {
    const url = new URL(value);
    return url.protocol === 'http:' || url.protocol === 'https:';
  } catch {
    return false;
  }
}

// Mirrors ApiConnectionSettings + StoryGenerationSettings + RetrySettings + ContentLimitSettings
// validation from SettingsValidator.Validate. Returns a field-name -> message map; empty means valid.
export function validateSettings(settings: AppSettings): Record<string, string> {
  const errors: Record<string, string> = {};

  if (settings.baseUrl && !isAbsoluteHttpUrl(settings.baseUrl)) {
    errors['baseUrl'] = 'Must be an absolute http or https URL.';
  }

  range(errors, 'requestTimeoutSeconds', settings.requestTimeoutSeconds, 10, 900);
  range(errors, 'maxOutputTokens', settings.maxOutputTokens, 256, 131072);
  optionalRange(errors, 'temperature', settings.temperature, 0, 2);
  optionalRange(errors, 'topP', settings.topP, 0, 1);

  range(errors, 'recentTurnCount', settings.recentTurnCount, 0, 100);
  range(errors, 'maxStoryBibleEntries', settings.maxStoryBibleEntries, 1, MAX_STORY_BIBLE_ENTRIES_UPPER_BOUND);
  range(errors, 'maxStoryBibleEntryCharacters', settings.maxStoryBibleEntryCharacters, 100, 50000);
  range(errors, 'maxStoryBibleCharacters', settings.maxStoryBibleCharacters, 1000, 1000000);
  if (Number.isFinite(settings.maxStoryBibleEntryCharacters) && Number.isFinite(settings.maxStoryBibleCharacters)
    && settings.maxStoryBibleEntryCharacters > settings.maxStoryBibleCharacters) {
    errors['maxStoryBibleEntryCharacters'] = 'Must not exceed the maximum total Story Bible characters.';
  }
  range(errors, 'storyBibleWarningPercent', settings.storyBibleWarningPercent, 50, 95);

  range(errors, 'maxPlannedEvents', settings.maxPlannedEvents, 1, MAX_PLANNED_EVENTS_UPPER_BOUND);
  range(errors, 'maxPlannedEventCharacters', settings.maxPlannedEventCharacters, 100, 50000);
  range(errors, 'maxPlannedEventsCharacters', settings.maxPlannedEventsCharacters, 1000, 1000000);
  if (Number.isFinite(settings.maxPlannedEventCharacters) && Number.isFinite(settings.maxPlannedEventsCharacters)
    && settings.maxPlannedEventCharacters > settings.maxPlannedEventsCharacters) {
    errors['maxPlannedEventCharacters'] = 'Must not exceed the maximum total Planned Events characters.';
  }
  range(errors, 'plannedEventsWarningPercent', settings.plannedEventsWarningPercent, 50, 95);

  range(errors, 'maxAutomaticRetries', settings.maxAutomaticRetries, 0, 5);
  range(errors, 'retryInitialDelaySeconds', settings.retryInitialDelaySeconds, .25, 30);
  range(errors, 'retryMaxDelaySeconds', settings.retryMaxDelaySeconds, 1, 120);
  range(errors, 'retryMaxRetryAfterSeconds', settings.retryMaxRetryAfterSeconds, 1, 600);
  if (Number.isFinite(settings.retryMaxDelaySeconds) && Number.isFinite(settings.retryInitialDelaySeconds)
    && settings.retryMaxDelaySeconds < settings.retryInitialDelaySeconds) {
    errors['retryMaxDelaySeconds'] = 'Maximum retry delay must be at least the initial delay.';
  }

  range(errors, 'maxStoryTitleCharacters', settings.maxStoryTitleCharacters, 1, 1000);
  range(errors, 'maxStoryLabelCharacters', settings.maxStoryLabelCharacters, 1, 1000);
  range(errors, 'maxStoryPromptCharacters', settings.maxStoryPromptCharacters, 100, 200000);
  range(errors, 'maxPlayerActionCharacters', settings.maxPlayerActionCharacters, 1, 50000);
  range(errors, 'maxNarrationCharacters', settings.maxNarrationCharacters, 100, 200000);

  range(errors, 'minSuggestedActions', settings.minSuggestedActions, 1, 20);
  range(errors, 'maxSuggestedActions', settings.maxSuggestedActions, 1, 20);
  if (Number.isFinite(settings.minSuggestedActions) && Number.isFinite(settings.maxSuggestedActions)
    && settings.minSuggestedActions > settings.maxSuggestedActions) {
    errors['minSuggestedActions'] = 'Must not exceed the maximum suggested actions.';
  }
  range(errors, 'maxSuggestedActionCharacters', settings.maxSuggestedActionCharacters, 1, 5000);

  range(errors, 'maxStoryBibleCategoryCharacters', settings.maxStoryBibleCategoryCharacters, 1, 1000);
  range(errors, 'maxStoryBibleNameCharacters', settings.maxStoryBibleNameCharacters, 1, 2000);
  range(errors, 'maxStoryBibleUpdatesPerResponse', settings.maxStoryBibleUpdatesPerResponse, 1, 1000);
  range(errors, 'maxPlannedEventDescriptionCharacters', settings.maxPlannedEventDescriptionCharacters, 1, 5000);
  range(errors, 'maxPlannedEventConditionCharacters', settings.maxPlannedEventConditionCharacters, 1, 5000);
  range(errors, 'maxPlannedEventUpdatesPerResponse', settings.maxPlannedEventUpdatesPerResponse, 1, 1000);
  range(errors, 'maxConditions', settings.maxConditions, 1, MAX_CONDITIONS_UPPER_BOUND);
  range(errors, 'maxConditionDescriptionCharacters', settings.maxConditionDescriptionCharacters, 1, 5000);

  range(errors, 'minParagraphs', settings.minParagraphs, 1, 20);
  range(errors, 'maxParagraphs', settings.maxParagraphs, 1, 20);
  if (Number.isFinite(settings.minParagraphs) && Number.isFinite(settings.maxParagraphs)
    && settings.minParagraphs > settings.maxParagraphs) {
    errors['minParagraphs'] = 'Must not exceed the maximum paragraphs per response.';
  }

  range(errors, 'minSentencesPerParagraph', settings.minSentencesPerParagraph, 1, 20);
  range(errors, 'maxSentencesPerParagraph', settings.maxSentencesPerParagraph, 1, 20);
  if (Number.isFinite(settings.minSentencesPerParagraph) && Number.isFinite(settings.maxSentencesPerParagraph)
    && settings.minSentencesPerParagraph > settings.maxSentencesPerParagraph) {
    errors['minSentencesPerParagraph'] = 'Must not exceed the maximum sentences per paragraph.';
  }

  return errors;
}
