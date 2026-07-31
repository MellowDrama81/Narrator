import { afterEach, vi } from 'vitest';
import { defaultSettings } from './defaults';
import { LlmService } from './llm.service';
import { AppSettings, StoryState } from './models';

const settings = (): AppSettings => ({
  ...defaultSettings(),
  baseUrl: 'https://example.test/v1',
  modelId: 'test-model',
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
  },
  currentStoryBible: [],
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
    completedAtUtc: '2026-01-01T00:01:00.000Z',
    modelId: 'test-model',
  }],
});

const completion = (suggestedActions = ['Search the desk', 'Climb the stairs', 'Check the generator']): Response =>
  new Response(JSON.stringify({
    choices: [{
      message: {
        content: JSON.stringify({
          turnNumber: 1,
          acknowledgedPlayerAction: 'Search for a light',
          narration: 'You find a lantern beneath the desk.',
          suggestedActions,
          relevantStoryBibleEntryIds: [],
          storyBibleUpdates: [],
        }),
      },
    }],
  }), { status: 200, headers: { 'Content-Type': 'application/json' } });

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

    const result = await new LlmService().turn(settings(), story(), 'Search for a light');

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
    expect(messages.map(message => message.role)).toEqual(['system', 'user', 'user', 'assistant', 'user']);
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
  });

  it('remembers when a provider requires JSON mode instead', async () => {
    const requests: Array<Record<string, unknown>> = [];
    const fetchMock = vi.fn(async (_input: RequestInfo | URL, init?: RequestInit) => {
      requests.push(JSON.parse(String(init?.body)));
      return requests.length === 1
        ? new Response('json_schema is unsupported', { status: 400 })
        : completion();
    });
    vi.stubGlobal('fetch', fetchMock);
    const service = new LlmService();

    await service.turn(settings(), story(), 'Search for a light');
    await service.turn(settings(), story(), 'Search for a light');

    expect(requests).toHaveLength(3);
    expect((requests[0]['response_format'] as { type: string }).type).toBe('json_schema');
    expect((requests[1]['response_format'] as { type: string }).type).toBe('json_object');
    expect((requests[2]['response_format'] as { type: string }).type).toBe('json_object');
  });
});
