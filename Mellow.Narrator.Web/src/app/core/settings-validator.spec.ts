import { defaultSettings } from './defaults';
import { AppSettings } from './models';
import { validateSettings } from './settings-validator';

const settings = (overrides: Partial<AppSettings> = {}): AppSettings => ({ ...defaultSettings(), ...overrides });

describe('validateSettings', () => {
  it('accepts the defaults', () => {
    expect(validateSettings(settings())).toEqual({});
  });

  it('accepts every inclusive lower boundary', () => {
    expect(validateSettings(settings({
      requestTimeoutSeconds: 10,
      maxOutputTokens: 256,
      temperature: 0,
      topP: 0,
      recentTurnCount: 0,
      maxStoryBibleEntries: 1,
      maxStoryBibleEntryCharacters: 100,
      maxStoryBibleCharacters: 1000,
      storyBibleWarningPercent: 50,
      maxPlannedEvents: 1,
      maxPlannedEventCharacters: 100,
      maxPlannedEventsCharacters: 1000,
      plannedEventsWarningPercent: 50,
      maxAutomaticRetries: 0,
      retryInitialDelaySeconds: .25,
      retryMaxDelaySeconds: 1,
      retryMaxRetryAfterSeconds: 1,
      maxStoryTitleCharacters: 1,
      maxStoryLabelCharacters: 1,
      maxStoryPromptCharacters: 100,
      maxPlayerActionCharacters: 1,
      maxNarrationCharacters: 100,
      minSuggestedActions: 1,
      maxSuggestedActions: 1,
      maxSuggestedActionCharacters: 1,
      maxStoryBibleCategoryCharacters: 1,
      maxStoryBibleNameCharacters: 1,
      maxStoryBibleUpdatesPerResponse: 1,
      maxPlannedEventDescriptionCharacters: 1,
      maxPlannedEventConditionCharacters: 1,
      maxPlannedEventUpdatesPerResponse: 1,
      maxConditions: 1,
      maxConditionDescriptionCharacters: 1,
      maxStorySummaryCharacters: 500,
      minParagraphs: 1,
      maxParagraphs: 1,
      minSentencesPerParagraph: 1,
      maxSentencesPerParagraph: 1,
    }))).toEqual({});
  });

  it('accepts every inclusive upper boundary', () => {
    expect(validateSettings(settings({
      requestTimeoutSeconds: 900,
      maxOutputTokens: 131072,
      temperature: 2,
      topP: 1,
      recentTurnCount: 100,
      maxStoryBibleEntries: 2000,
      maxStoryBibleEntryCharacters: 50000,
      maxStoryBibleCharacters: 1000000,
      storyBibleWarningPercent: 95,
      maxPlannedEvents: 500,
      maxPlannedEventCharacters: 50000,
      maxPlannedEventsCharacters: 1000000,
      plannedEventsWarningPercent: 95,
      maxAutomaticRetries: 5,
      retryInitialDelaySeconds: 30,
      retryMaxDelaySeconds: 120,
      retryMaxRetryAfterSeconds: 600,
      maxStoryTitleCharacters: 1000,
      maxStoryLabelCharacters: 1000,
      maxStoryPromptCharacters: 200000,
      maxPlayerActionCharacters: 50000,
      maxNarrationCharacters: 200000,
      minSuggestedActions: 20,
      maxSuggestedActions: 20,
      maxSuggestedActionCharacters: 5000,
      maxStoryBibleCategoryCharacters: 1000,
      maxStoryBibleNameCharacters: 2000,
      maxStoryBibleUpdatesPerResponse: 1000,
      maxPlannedEventDescriptionCharacters: 5000,
      maxPlannedEventConditionCharacters: 5000,
      maxPlannedEventUpdatesPerResponse: 1000,
      maxConditions: 200,
      maxConditionDescriptionCharacters: 5000,
      maxStorySummaryCharacters: 20000,
      minParagraphs: 20,
      maxParagraphs: 20,
      minSentencesPerParagraph: 20,
      maxSentencesPerParagraph: 20,
    }))).toEqual({});
  });

  it('rejects a value just below the lower bound', () => {
    expect(validateSettings(settings({ requestTimeoutSeconds: 9 }))).toHaveProperty('requestTimeoutSeconds');
  });

  it('rejects a value just above the upper bound', () => {
    expect(validateSettings(settings({ maxOutputTokens: 131073 }))).toHaveProperty('maxOutputTokens');
  });

  it('does not clamp invalid values', () => {
    const value = settings({ maxOutputTokens: 1 });
    const errors = validateSettings(value);
    expect(errors).toHaveProperty('maxOutputTokens');
    expect(value.maxOutputTokens).toBe(1);
  });

  it('rejects NaN from a blank number input, not silently', () => {
    expect(validateSettings(settings({ maxOutputTokens: NaN }))).toHaveProperty('maxOutputTokens');
    expect(validateSettings(settings({ requestTimeoutSeconds: NaN }))).toHaveProperty('requestTimeoutSeconds');
    expect(validateSettings(settings({ minSuggestedActions: NaN }))).toHaveProperty('minSuggestedActions');
  });

  it('rejects non-finite values', () => {
    expect(validateSettings(settings({ maxOutputTokens: Infinity }))).toHaveProperty('maxOutputTokens');
    expect(validateSettings(settings({ maxOutputTokens: -Infinity }))).toHaveProperty('maxOutputTokens');
  });

  it('accepts a null optional temperature/topP', () => {
    expect(validateSettings(settings({ maxOutputTokens: null, temperature: null, topP: null }))).toEqual({});
  });

  it('rejects an out-of-range optional temperature', () => {
    expect(validateSettings(settings({ temperature: 2.1 }))).toHaveProperty('temperature');
  });

  it('rejects minSuggestedActions above maxSuggestedActions', () => {
    const errors = validateSettings(settings({ minSuggestedActions: 5, maxSuggestedActions: 3 }));
    expect(errors).toHaveProperty('minSuggestedActions');
  });

  it('rejects minParagraphs above maxParagraphs', () => {
    const errors = validateSettings(settings({ minParagraphs: 6, maxParagraphs: 4 }));
    expect(errors).toHaveProperty('minParagraphs');
  });

  it('rejects minSentencesPerParagraph above maxSentencesPerParagraph', () => {
    const errors = validateSettings(settings({ minSentencesPerParagraph: 6, maxSentencesPerParagraph: 4 }));
    expect(errors).toHaveProperty('minSentencesPerParagraph');
  });

  it('rejects maxStoryBibleEntryCharacters above maxStoryBibleCharacters', () => {
    const errors = validateSettings(settings({ maxStoryBibleEntryCharacters: 2000, maxStoryBibleCharacters: 1000 }));
    expect(errors).toHaveProperty('maxStoryBibleEntryCharacters');
  });

  it('rejects maxPlannedEventCharacters above maxPlannedEventsCharacters', () => {
    const errors = validateSettings(settings({ maxPlannedEventCharacters: 2000, maxPlannedEventsCharacters: 1000 }));
    expect(errors).toHaveProperty('maxPlannedEventCharacters');
  });

  it('rejects maxStorySummaryCharacters outside its 500..20000 range', () => {
    expect(validateSettings(settings({ maxStorySummaryCharacters: 499 }))).toHaveProperty('maxStorySummaryCharacters');
    expect(validateSettings(settings({ maxStorySummaryCharacters: 20001 }))).toHaveProperty('maxStorySummaryCharacters');
  });

  it('rejects retryMaxDelaySeconds below retryInitialDelaySeconds', () => {
    const errors = validateSettings(settings({ retryInitialDelaySeconds: 10, retryMaxDelaySeconds: 5 }));
    expect(errors).toHaveProperty('retryMaxDelaySeconds');
  });

  it('accepts an empty baseUrl', () => {
    expect(validateSettings(settings({ baseUrl: '' }))).toEqual({});
  });

  it('accepts an absolute https baseUrl', () => {
    expect(validateSettings(settings({ baseUrl: 'https://provider.example/v1' }))).toEqual({});
  });

  it('rejects a relative baseUrl', () => {
    expect(validateSettings(settings({ baseUrl: '/v1' }))).toHaveProperty('baseUrl');
  });

  it('rejects a non-http baseUrl scheme', () => {
    expect(validateSettings(settings({ baseUrl: 'ftp://provider.example/v1' }))).toHaveProperty('baseUrl');
  });
});
