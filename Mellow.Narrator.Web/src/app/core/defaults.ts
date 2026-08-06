import { AppSettings } from './models';

export const defaultSettings = (): AppSettings => ({
  key: 'app',
  baseUrl: 'https://api.openai.com/v1',
  modelId: '',
  apiKey: '',
  requestTimeoutSeconds: 120,
  maxOutputTokens: 4096,
  temperature: null,
  topP: null,
  reasoningEffort: '',
  recentTurnCount: 8,
  maxStoryBibleEntries: 200,
  maxPlannedEvents: 50,
  plannedEventsWarningPercent: 80,
  minSuggestedActions: 2,
  maxSuggestedActions: 3,
  minParagraphs: 4,
  maxParagraphs: 6,
});

