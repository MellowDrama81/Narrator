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
  turnPipeline: 'oneCall',
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
  storySummary: 'A storm has trapped you in an observatory.',
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
      storySummary: 'You found a lantern in the observatory.',
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
  it('uses four calls when the multi-call turn pipeline is enabled', async () => {
    let call = 0;
    vi.stubGlobal('fetch', vi.fn(async () => {
      call++;
      const content = call === 1 ? { result: 'The planned event is not yet eligible.' }
        : call === 2 ? { result: 'Resolve the lantern search and end with a decision.' }
        : call === 3 ? { narration: 'You find a lantern beneath the desk.', suggestedActions: ['Search the desk', 'Climb the stairs'] }
        : {
            turnNumber: 1,
            acknowledgedPlayerAction: 'Search for a light',
            narration: 'Placeholder narration that will be replaced.',
            suggestedActions: ['Placeholder action'],
            relevantStoryBibleEntryIds: [], storyBibleUpdates: [], relevantPlannedEventIds: [], plannedEventUpdates: [],
            revealedVictoryConditionIds: [], metVictoryConditionIds: [], revealedLossConditionIds: [], metLossConditionIds: [],
            storySummary: 'You found a lantern in the observatory.',
          };
      return new Response(JSON.stringify({ choices: [{ message: { content: JSON.stringify(content) } }] }), { status: 200 });
    }));

    const result = await new LlmService(fakeDb()).turn(
      { ...settings(), turnPipeline: 'fourCalls' }, story(), 'Search for a light');

    expect(call).toBe(4);
    expect(result.narration).toBe('You find a lantern beneath the desk.');
    expect(result.suggestedActions).toEqual(['Search the desk', 'Climb the stairs']);
  });

  it('uses seven calls when the full multi-call pipeline is enabled', async () => {
    let call = 0;
    vi.stubGlobal('fetch', vi.fn(async () => {
      call++;
      const content = call === 3 ? { narration: 'You find a lantern beneath the desk.', suggestedActions: ['Search the desk', 'Climb the stairs'] }
        : call === 7 ? {
            turnNumber: 1, acknowledgedPlayerAction: 'Search for a light', narration: 'Placeholder.', suggestedActions: ['Placeholder'],
            relevantStoryBibleEntryIds: [], storyBibleUpdates: [], relevantPlannedEventIds: [], plannedEventUpdates: [],
            revealedVictoryConditionIds: [], metVictoryConditionIds: [], revealedLossConditionIds: [], metLossConditionIds: [],
            storySummary: 'You found a lantern in the observatory.',
          }
        : { result: `Internal analysis ${call}.` };
      return new Response(JSON.stringify({ choices: [{ message: { content: JSON.stringify(content) } }] }), { status: 200 });
    }));

    const result = await new LlmService(fakeDb()).turn(
      { ...settings(), turnPipeline: 'sevenCalls' }, story(), 'Search for a light');

    expect(call).toBe(7);
    expect(result.narration).toBe('You find a lantern beneath the desk.');
    expect(result.suggestedActions).toEqual(['Search the desk', 'Climb the stairs']);
  });

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
      .rejects.toThrow(/must set acknowledgedPlayerAction/);
    expect(fetchMock).toHaveBeenCalledTimes(2);
  });

  // Some models, when not constrained by a strict JSON schema, mistakenly echo the request's field
  // name (currentPlayerAction) instead of the response's actual field name (acknowledgedPlayerAction) -
  // observed in practice against a real provider even when the copied text itself was exactly correct.
  it('accepts currentPlayerAction as a fallback when acknowledgedPlayerAction is missing entirely', async () => {
    const fetchMock = vi.fn(async () => turnResponse({
      acknowledgedPlayerAction: undefined,
      currentPlayerAction: 'Search for a light',
    }));
    vi.stubGlobal('fetch', fetchMock);

    const result = await new LlmService(fakeDb()).turn(settings(), story(), 'Search for a light');

    expect(result.acknowledgedPlayerAction).toBe('Search for a light');
    expect(fetchMock).toHaveBeenCalledTimes(1);
  });

  it('sends the current story summary and requires one back within the configured limit', async () => {
    const requests: Array<Record<string, unknown>> = [];
    vi.stubGlobal('fetch', vi.fn(async (_input: RequestInfo | URL, init?: RequestInit) => {
      requests.push(JSON.parse(String(init?.body)));
      return completion();
    }));
    const currentStory = story();
    currentStory.storySummary = 'A storm has trapped you in an observatory.';

    const result = await new LlmService(fakeDb()).turn(settings(), currentStory, 'Search for a light');

    const messages = requests[0]['messages'] as Array<{ role: string; content: string }>;
    expect(JSON.parse(messages[1].content)).toMatchObject({ storySummary: 'A storm has trapped you in an observatory.' });
    const format = requests[0]['response_format'] as {
      json_schema: { schema: { properties: { storySummary: { maxLength: number } }; required: string[] } };
    };
    expect(format.json_schema.schema.properties.storySummary).toMatchObject({ maxLength: settings().maxStorySummaryCharacters });
    expect(format.json_schema.schema.required).toContain('storySummary');
    expect(result.storySummary).toBe('You found a lantern in the observatory.');
  });

  it('sends an empty story summary for the opening scene', async () => {
    const requests: Array<Record<string, unknown>> = [];
    vi.stubGlobal('fetch', vi.fn(async (_input: RequestInfo | URL, init?: RequestInit) => {
      requests.push(JSON.parse(String(init?.body)));
      return new Response(JSON.stringify({
        choices: [{ message: { content: JSON.stringify({
          turnNumber: 0,
          acknowledgedPlayerAction: null,
          narration: 'You wake in the observatory as the storm breaks.',
          suggestedActions: ['Stand up', 'Call out'],
          relevantStoryBibleEntryIds: [],
          storyBibleUpdates: [],
          storySummary: 'You woke in the observatory during a storm.',
        }) } }],
      }), { status: 200, headers: { 'Content-Type': 'application/json' } });
    }));
    const emptyStory = story();
    emptyStory.turns = [];

    await new LlmService(fakeDb()).opening(settings(), {
      id: 'def-1', ...emptyStory.definition,
      sortOrder: 0, createdAtUtc: '2026-01-01T00:00:00.000Z', updatedAtUtc: '2026-01-01T00:00:00.000Z',
    });

    const messages = requests[0]['messages'] as Array<{ role: string; content: string }>;
    expect(JSON.parse(messages[1].content)).toMatchObject({ storySummary: '' });
  });

  it('performs a corrective retry when the returned story summary is missing, then succeeds', async () => {
    const requests: Array<Record<string, unknown>> = [];
    const fetchMock = vi.fn(async (_input: RequestInfo | URL, init?: RequestInit) => {
      requests.push(JSON.parse(String(init?.body)));
      return requests.length === 1
        ? turnResponse({ storySummary: undefined })
        : completion();
    });
    vi.stubGlobal('fetch', fetchMock);

    const result = await new LlmService(fakeDb()).turn(settings(), story(), 'Search for a light');

    expect(requests).toHaveLength(2);
    const correctiveMessage = (requests[1]['messages'] as Array<{ content: string }>).at(-1);
    expect(correctiveMessage?.content).toContain('story summary is missing');
    expect(result.storySummary).toBe('You found a lantern in the observatory.');
  });

  it('performs a corrective retry when the returned story summary exceeds the configured limit', async () => {
    const requests: Array<Record<string, unknown>> = [];
    const fetchMock = vi.fn(async (_input: RequestInfo | URL, init?: RequestInit) => {
      requests.push(JSON.parse(String(init?.body)));
      return requests.length === 1
        ? turnResponse({ storySummary: 'x'.repeat(settings().maxStorySummaryCharacters + 1) })
        : completion();
    });
    vi.stubGlobal('fetch', fetchMock);

    const result = await new LlmService(fakeDb()).turn(settings(), story(), 'Search for a light');

    expect(requests).toHaveLength(2);
    const correctiveMessage = (requests[1]['messages'] as Array<{ content: string }>).at(-1);
    expect(correctiveMessage?.content).toContain('story summary is missing or exceeds the configured limit');
    expect(result.storySummary).toBe('You found a lantern in the observatory.');
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
