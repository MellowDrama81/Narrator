import { afterEach, vi } from 'vitest';
import { DbService } from './db.service';
import { defaultSettings } from './defaults';
import { LlmService } from './llm.service';
import { AppSettings, StoryState } from './models';

// LlmService only ever calls db.saveSettings (to persist a newly-learned structured-output tier/request
// contract) - never db.settings(), since settings are always handed to it by the caller.
function fakeDb(): DbService {
  return { saveSettings: vi.fn(async () => {}) } as unknown as DbService;
}

const settings = (): AppSettings => ({
  ...defaultSettings(),
  baseUrl: 'https://example.test/v1',
  modelId: 'test-model',
  // Fast enough that the HTTP-retry backoff tests don't actually wait real seconds.
  retryInitialDelaySeconds: 0.01,
  retryMaxDelaySeconds: 0.02,
});

const story = (): StoryState => ({
  id: 'story-id',
  label: 'Test story',
  sourceStoryDefinitionId: null,
  definition: {
    title: 'Test story',
    storyPrompt: 'A storm strands you in an abandoned observatory.',
    initialEventsPrompt: 'Open with the storm breaking the front door.',
    initialStoryBible: [],
    initialPlannedEvents: [],
    initialVictoryConditions: [],
    initialLossConditions: [],
  },
  currentStoryBible: [],
  currentPlannedEvents: [],
  currentVictoryConditions: [],
  currentLossConditions: [],
  revealedVictoryConditionIds: [],
  metVictoryConditionIds: [],
  revealedLossConditionIds: [],
  metLossConditionIds: [],
  sortOrder: 0,
  startedAtUtc: '2026-01-01T00:00:00.000Z',
  lastActionAtUtc: '2026-01-01T00:01:00.000Z',
  turns: [{
    id: 'turn-id',
    storyStateId: 'story-id',
    sequenceNumber: 0,
    playerAction: null,
    narration: 'Rain drums against the observatory dome.',
    suggestedActions: ['Inspect the telescope', 'Barricade the door'],
    relevantStoryBibleEntryIds: [],
    storyBibleUpdates: [],
    relevantPlannedEventIds: [],
    plannedEventUpdates: [],
    revealedVictoryConditionIds: [],
    metVictoryConditionIds: [],
    revealedLossConditionIds: [],
    metLossConditionIds: [],
    completedAtUtc: '2026-01-01T00:01:00.000Z',
    modelId: 'test-model',
  }],
});

function turnResponse(fields: Record<string, unknown>): Response {
  return new Response(JSON.stringify({
    choices: [{ message: { content: JSON.stringify({
      turnNumber: 1,
      acknowledgedPlayerAction: 'Search for a light',
      narration: 'You find a lantern beneath the desk.',
      suggestedActions: ['Search the desk', 'Climb the stairs', 'Check the generator'],
      relevantStoryBibleEntryIds: [],
      storyBibleUpdates: [],
      ...fields,
    }) } }],
  }), { status: 200, headers: { 'Content-Type': 'application/json' } });
}

const completion = (suggestedActions = ['Search the desk', 'Climb the stairs', 'Check the generator']): Response =>
  turnResponse({ suggestedActions });

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('LlmService', () => {
  it('uses the configured suggestion range in a strict turn schema', async () => {
    const requests: Array<Record<string, unknown>> = [];
    vi.stubGlobal('fetch', vi.fn(async (_input: RequestInfo | URL, init?: RequestInit) => {
      requests.push(JSON.parse(String(init?.body)));
      return completion();
    }));

    const result = await new LlmService(fakeDb()).turn(settings(), story(), 'Search for a light');

    expect(result.suggestedActions).toHaveLength(3);
    const format = requests[0]['response_format'] as {
      type: string;
      json_schema: { schema: { properties: { suggestedActions: { minItems: number; maxItems: number } } } };
    };
    expect(format.type).toBe('json_schema');
    expect(format.json_schema.schema.properties.suggestedActions).toMatchObject({
      minItems: 2,
      maxItems: 3,
    });

    const messages = requests[0]['messages'] as Array<{ role: string; content: string }>;
    // instructionMessageRole defaults to 'developer', so our internally-tagged 'system' message is sent
    // as role 'developer' on the wire.
    expect(messages.map(message => message.role)).toEqual(['developer', 'user', 'user', 'assistant', 'user']);
    expect(JSON.parse(messages[1].content)).toMatchObject({ contextType: 'storyContext' });
    expect(JSON.parse(messages.at(-1)!.content)).toMatchObject({
      requestType: 'storyTurn',
      currentPlayerAction: 'Search for a light',
    });
    expect(JSON.parse(messages.at(-1)!.content).resolutionRoll).toBeGreaterThanOrEqual(1);
    expect(JSON.parse(messages.at(-1)!.content).resolutionRoll).toBeLessThanOrEqual(100);
    expect(messages[0].content).toContain('Choose the difficulty before considering resolutionRoll');
    expect(messages[0].content).toContain('ordinary human attempting to levitate');
    expect(messages[0].content).toContain('the player controls only their own character');
    expect(messages[0].content).toContain('the guard gives me the key');
    expect(requests[0]['max_completion_tokens']).toBe(settings().maxOutputTokens);
  });

  it('falls back from strict JSON schema through JSON mode to PromptedJson, and persists the working tier', async () => {
    const requests: Array<Record<string, unknown>> = [];
    const fetchMock = vi.fn(async (_input: RequestInfo | URL, init?: RequestInit) => {
      requests.push(JSON.parse(String(init?.body)));
      return requests.length <= 2
        ? new Response('response_format is unsupported', { status: 400 })
        : completion();
    });
    vi.stubGlobal('fetch', fetchMock);
    const db = fakeDb();

    const result = await new LlmService(db).turn(settings(), story(), 'Search for a light');

    expect(requests).toHaveLength(3);
    expect((requests[0]['response_format'] as { type: string }).type).toBe('json_schema');
    expect((requests[1]['response_format'] as { type: string }).type).toBe('json_object');
    expect(requests[2]['response_format']).toBeUndefined();
    // PromptedJson appends a system message embedding the schema and a synthesized example, with no
    // response_format hint at all.
    const promptedMessages = requests[2]['messages'] as Array<{ role: string; content: string }>;
    expect(promptedMessages.at(-1)?.content).toContain('Return an object matching this JSON Schema exactly');
    expect(promptedMessages.at(-1)?.content).toContain('"suggestedActions"');
    expect(result.narration).toBe('You find a lantern beneath the desk.');
    expect(db.saveSettings).toHaveBeenCalledWith(expect.objectContaining({ structuredOutputTier: 'promptedJson' }));
  });

  it('skips previously-failed structured-output tiers on a later request once persisted', async () => {
    const requests: Array<Record<string, unknown>> = [];
    const fetchMock = vi.fn(async (_input: RequestInfo | URL, init?: RequestInit) => {
      requests.push(JSON.parse(String(init?.body)));
      return requests.length === 1
        ? new Response('json_schema is unsupported', { status: 400 })
        : completion();
    });
    vi.stubGlobal('fetch', fetchMock);
    const shared = settings();
    const service = new LlmService(fakeDb());

    await service.turn(shared, story(), 'Search for a light');
    expect(requests).toHaveLength(2);
    expect(shared.structuredOutputTier).toBe('jsonMode');

    await service.turn(shared, story(), 'Search for a light');
    expect(requests).toHaveLength(3);
    expect((requests[2]['response_format'] as { type: string }).type).toBe('json_object');
  });

  it('retries a transient HTTP failure with backoff before succeeding', async () => {
    let attempts = 0;
    const fetchMock = vi.fn(async () => {
      attempts++;
      return attempts < 3 ? new Response('rate limited', { status: 429 }) : completion();
    });
    vi.stubGlobal('fetch', fetchMock);

    const result = await new LlmService(fakeDb()).turn(settings(), story(), 'Search for a light');

    expect(attempts).toBe(3);
    expect(result.narration).toBe('You find a lantern beneath the desk.');
  });

  it('gives up after exhausting maxAutomaticRetries on a persistent transient failure', async () => {
    const fetchMock = vi.fn(async () => new Response('still rate limited', { status: 429 }));
    vi.stubGlobal('fetch', fetchMock);
    const withFewRetries = { ...settings(), maxAutomaticRetries: 1 };

    await expect(new LlmService(fakeDb()).turn(withFewRetries, story(), 'Search for a light')).rejects.toThrow();
    // maxAutomaticRetries=1 means at most 2 attempts per structured-output tier candidate.
    expect(fetchMock).toHaveBeenCalledTimes(2);
  });

  it('performs one corrective retry when the model returns an incomplete turn, then succeeds', async () => {
    const requests: Array<Record<string, unknown>> = [];
    const fetchMock = vi.fn(async (_input: RequestInfo | URL, init?: RequestInit) => {
      requests.push(JSON.parse(String(init?.body)));
      return requests.length === 1
        ? turnResponse({ narration: '', suggestedActions: [] })
        : completion();
    });
    vi.stubGlobal('fetch', fetchMock);

    const result = await new LlmService(fakeDb()).turn(settings(), story(), 'Search for a light');

    expect(requests).toHaveLength(2);
    const correctiveMessage = (requests[1]['messages'] as Array<{ content: string }>).at(-1);
    expect(correctiveMessage?.content).toContain('Your previous response failed validation');
    expect(correctiveMessage?.content).toContain('Narration is empty');
    expect(result.narration).toBe('You find a lantern beneath the desk.');
  });

  it('throws a clear error when the acknowledged turn never matches, even after the corrective retry', async () => {
    const fetchMock = vi.fn(async () => turnResponse({ turnNumber: 99 }));
    vi.stubGlobal('fetch', fetchMock);

    await expect(new LlmService(fakeDb()).turn(settings(), story(), 'Search for a light'))
      .rejects.toThrow(/acknowledged turn 99, but the current turn is 1/);
    expect(fetchMock).toHaveBeenCalledTimes(2);
  });

  it('throws a clear error when the acknowledged player action never matches the real one', async () => {
    const fetchMock = vi.fn(async () => turnResponse({ acknowledgedPlayerAction: 'Something else entirely' }));
    vi.stubGlobal('fetch', fetchMock);

    await expect(new LlmService(fakeDb()).turn(settings(), story(), 'Search for a light'))
      .rejects.toThrow(/acknowledged a different player action/);
    expect(fetchMock).toHaveBeenCalledTimes(2);
  });

  it('rejects narration that substantially duplicates a recent turn, then succeeds on the corrective retry', async () => {
    const previousNarration = 'Rain drums against the observatory dome as thunder rolls low across the shuttered windows while shadows stretch long over the dusty telescope stand and old brass fittings gleam faintly in the storm light outside.';
    const currentStory = story();
    currentStory.turns[0].narration = previousNarration;
    const requests: Array<Record<string, unknown>> = [];
    const fetchMock = vi.fn(async (_input: RequestInfo | URL, init?: RequestInit) => {
      requests.push(JSON.parse(String(init?.body)));
      return requests.length === 1 ? turnResponse({ narration: previousNarration }) : completion();
    });
    vi.stubGlobal('fetch', fetchMock);

    const result = await new LlmService(fakeDb()).turn(settings(), currentStory, 'Search for a light');

    expect(requests).toHaveLength(2);
    const correctiveMessage = (requests[1]['messages'] as Array<{ content: string }>).at(-1);
    expect(correctiveMessage?.content).toContain('duplicates a recent scene');
    expect(result.narration).not.toBe(previousNarration);
  });
});
