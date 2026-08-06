import { Injectable } from '@angular/core';
import { AppSettings, DefinitionGeneration, StoryCondition, StoryDefinition, StoryState, TurnGeneration } from './models';
import { plannedEventCapacity } from './planned-events';
import { promptTemplates } from './prompt-templates.generated';
import { conditionPayload, normalizeConditionIds } from './story-conditions';

// Bundles one condition list (victory or loss) with the running totals generateTurn needs to build the
// outgoing storyContext payload and to leniently normalize the model's response - see conditionPayload
// and normalizeConditionIds in story-conditions.ts.
interface ConditionsContext {
  conditions: StoryCondition[];
  revealedIds: string[];
  metIds: string[];
}

type JsonSchema = Record<string, unknown>;

class ProviderHttpError extends Error {
  constructor(readonly status: number, message: string) {
    super(message);
  }
}

@Injectable({ providedIn: 'root' })
export class LlmService {
  private readonly strictSchemaSupport = new Map<string, boolean>();

  async loadModels(settings: AppSettings): Promise<string[]> {
    const response = await this.fetch(settings, 'models', { method: 'GET' });
    const body = await response.json() as { data?: Array<{ id?: string }> };
    return (body.data ?? []).map(x => x.id ?? '').filter(Boolean).sort();
  }

  async test(settings: AppSettings): Promise<string> {
    if (!settings.modelId) throw new Error('Choose or enter a model first.');
    await this.complete(settings, [
      { role: 'system', content: 'Return JSON only.' },
      { role: 'user', content: 'Return {"status":"ok"}.' },
    ], this.objectSchema({
      status: { type: 'string', enum: ['ok'] },
    }));
    return `Connected to ${settings.modelId}.`;
  }

  async generateDefinition(settings: AppSettings, storyPrompt: string): Promise<DefinitionGeneration> {
    const value = await this.complete(settings, [
      { role: 'system', content: promptTemplates.storyDefinitionInstruction },
      { role: 'user', content: storyPrompt },
    ], this.definitionSchema());
    const result = value as Partial<DefinitionGeneration>;
    if (!result.refinedStoryPrompt || !result.suggestedTitle || !Array.isArray(result.initialStoryBibleEntries))
      throw new Error('The model returned an incomplete Story Definition.');
    return {
      refinedStoryPrompt: result.refinedStoryPrompt,
      suggestedTitle: result.suggestedTitle,
      initialEventsPrompt: result.initialEventsPrompt ?? '',
      initialStoryBibleEntries: result.initialStoryBibleEntries.map(entry => ({
        category: String(entry.category ?? '').trim(),
        name: String(entry.name ?? '').trim(),
        knownFacts: Array.isArray(entry.knownFacts) ? entry.knownFacts.map(String) : [],
        secretFacts: Array.isArray(entry.secretFacts) ? entry.secretFacts.map(String) : [],
        importance: Math.min(5, Math.max(1, Number(entry.importance) || 3)),
      })).filter(entry => entry.name && (entry.knownFacts.length || entry.secretFacts.length)),
      initialPlannedEvents: Array.isArray(result.initialPlannedEvents)
        ? result.initialPlannedEvents.map(entry => ({
            description: String(entry.description ?? '').trim(),
            importance: Math.min(5, Math.max(1, Number(entry.importance) || 3)),
            urgency: Math.min(5, Math.max(1, Number(entry.urgency) || 3)),
            condition: entry.condition && String(entry.condition).trim() ? String(entry.condition).trim() : null,
          })).filter(entry => entry.description)
        : [],
      initialVictoryConditions: this.parseProposedConditions(result.initialVictoryConditions),
      initialLossConditions: this.parseProposedConditions(result.initialLossConditions),
    };
  }

  private parseProposedConditions(value: unknown): DefinitionGeneration['initialVictoryConditions'] {
    return Array.isArray(value)
      ? value.map(entry => ({
          description: String((entry as { description?: unknown })?.description ?? '').trim(),
          secret: Boolean((entry as { secret?: unknown })?.secret),
        })).filter(entry => entry.description)
      : [];
  }

  opening(settings: AppSettings, definition: StoryDefinition): Promise<TurnGeneration> {
    return this.generateTurn(
      settings, definition, [], definition.initialStoryBible, definition.initialPlannedEvents,
      { conditions: definition.initialVictoryConditions, revealedIds: [], metIds: [] },
      { conditions: definition.initialLossConditions, revealedIds: [], metIds: [] },
      null,
    );
  }

  turn(settings: AppSettings, story: StoryState, action: string): Promise<TurnGeneration> {
    return this.generateTurn(
      settings,
      story.definition,
      story.turns.slice(-settings.recentTurnCount),
      story.currentStoryBible,
      story.currentPlannedEvents,
      { conditions: story.currentVictoryConditions, revealedIds: story.revealedVictoryConditionIds, metIds: story.metVictoryConditionIds },
      { conditions: story.currentLossConditions, revealedIds: story.revealedLossConditionIds, metIds: story.metLossConditionIds },
      action,
    );
  }

  private async generateTurn(
    settings: AppSettings,
    definition: Pick<StoryDefinition, 'storyPrompt' | 'initialEventsPrompt'>,
    turns: StoryState['turns'],
    bible: StoryState['currentStoryBible'],
    plannedEvents: StoryState['currentPlannedEvents'],
    victory: ConditionsContext,
    loss: ConditionsContext,
    action: string | null,
  ): Promise<TurnGeneration> {
    const next = turns.length ? turns[turns.length - 1].sequenceNumber + 1 : 0;
    const messages: Array<{ role: string; content: string }> = [
      { role: 'system', content: this.renderNarrationInstruction(settings) },
      {
        role: 'user',
        content: JSON.stringify({
          contextType: 'storyContext',
          storyPrompt: definition.storyPrompt,
          storyBible: bible,
          plannedEvents,
          plannedEventCapacity: plannedEventCapacity(plannedEvents, settings.maxPlannedEvents, settings.plannedEventsWarningPercent),
          victoryConditions: conditionPayload(victory.conditions, victory.revealedIds, victory.metIds),
          lossConditions: conditionPayload(loss.conditions, loss.revealedIds, loss.metIds),
        }),
      },
    ];
    if (turns.length < settings.recentTurnCount && definition.initialEventsPrompt) {
      messages.push({
        role: 'user',
        content: JSON.stringify({
          contextType: 'initialEvents',
          content: definition.initialEventsPrompt,
          instruction: 'Use this only to help narrate the earliest scenes. It stops being supplied once enough real history has accumulated, so never rely on it being available later; anything that must be remembered belongs in the Story Bible instead.',
        }),
      });
    }
    for (const turn of turns) {
      if (turn.playerAction !== null) messages.push({ role: 'user', content: turn.playerAction });
      messages.push({ role: 'assistant', content: turn.narration });
    }
    messages.push({
      role: 'user',
      content: JSON.stringify({
        requestType: turns.length === 0 ? 'openingScene' : 'storyTurn',
        turnNumber: next,
        currentPlayerAction: action,
        resolutionRoll: action === null ? null : this.resolutionRoll(),
        instruction: turns.length === 0
          ? `${promptTemplates.openingSceneInstruction} Copy turnNumber exactly into the response and set acknowledgedPlayerAction to null.`
          : `${promptTemplates.continueStoryInstruction} Resolve currentPlayerAction now. Do not answer an action from the preceding history and do not repeat an earlier scene. Advance beyond the last assistant narration. Copy turnNumber and currentPlayerAction exactly into the response fields.`,
      }),
    });

    const value = await this.complete(settings, messages, this.turnSchema(settings)) as Partial<TurnGeneration>;
    if (!value.narration || !Array.isArray(value.suggestedActions))
      throw new Error('The model returned an incomplete story turn.');
    return {
      turnNumber: next,
      acknowledgedPlayerAction: action,
      narration: value.narration,
      suggestedActions: value.suggestedActions.map(String).filter(Boolean).slice(0, settings.maxSuggestedActions),
      relevantStoryBibleEntryIds: Array.isArray(value.relevantStoryBibleEntryIds) ? value.relevantStoryBibleEntryIds.map(String) : [],
      storyBibleUpdates: Array.isArray(value.storyBibleUpdates) ? value.storyBibleUpdates : [],
      relevantPlannedEventIds: Array.isArray(value.relevantPlannedEventIds) ? value.relevantPlannedEventIds.map(String) : [],
      plannedEventUpdates: Array.isArray(value.plannedEventUpdates) ? value.plannedEventUpdates : [],
      revealedVictoryConditionIds: normalizeConditionIds(
        Array.isArray(value.revealedVictoryConditionIds) ? value.revealedVictoryConditionIds : [],
        victory.conditions, victory.revealedIds, victory.metIds, true,
      ),
      metVictoryConditionIds: normalizeConditionIds(
        Array.isArray(value.metVictoryConditionIds) ? value.metVictoryConditionIds : [],
        victory.conditions, victory.revealedIds, victory.metIds, false,
      ),
      revealedLossConditionIds: normalizeConditionIds(
        Array.isArray(value.revealedLossConditionIds) ? value.revealedLossConditionIds : [],
        loss.conditions, loss.revealedIds, loss.metIds, true,
      ),
      metLossConditionIds: normalizeConditionIds(
        Array.isArray(value.metLossConditionIds) ? value.metLossConditionIds : [],
        loss.conditions, loss.revealedIds, loss.metIds, false,
      ),
    };
  }

  private renderNarrationInstruction(settings: AppSettings): string {
    const replacements: Record<string, string | number> = {
      minSuggestedActions: settings.minSuggestedActions,
      maxSuggestedActions: settings.maxSuggestedActions,
      minParagraphs: settings.minParagraphs,
      maxParagraphs: settings.maxParagraphs,
      minSentences: 2,
      maxSentences: 5,
    };
    let instruction: string = promptTemplates.storyNarrationInstruction;
    for (const [name, value] of Object.entries(replacements))
      instruction = instruction.replaceAll(`{${name}}`, String(value));

    return instruction;
  }

  private async complete(
    settings: AppSettings,
    messages: Array<{ role: string; content: string }>,
    schema?: JsonSchema,
  ): Promise<unknown> {
    if (!settings.baseUrl) throw new Error('Configure an API base URL first.');
    if (!settings.modelId) throw new Error('Choose or enter a model first.');
    const supportKey = `${settings.baseUrl.replace(/\/+$/, '')}\n${settings.modelId}`;
    if (schema && this.strictSchemaSupport.get(supportKey) !== false) {
      try {
        const result = await this.requestCompletion(settings, messages, schema);
        this.strictSchemaSupport.set(supportKey, true);
        return result;
      } catch (error) {
        if (!(error instanceof ProviderHttpError) || ![400, 404, 422].includes(error.status)) throw error;
        this.strictSchemaSupport.set(supportKey, false);
      }
    }
    return this.requestCompletion(settings, messages);
  }

  private async requestCompletion(
    settings: AppSettings,
    messages: Array<{ role: string; content: string }>,
    schema?: JsonSchema,
  ): Promise<unknown> {
    const body: Record<string, unknown> = {
      model: settings.modelId,
      messages,
      max_tokens: settings.maxOutputTokens,
      response_format: schema
        ? { type: 'json_schema', json_schema: { name: 'mellow_narrator_response', strict: true, schema } }
        : { type: 'json_object' },
    };
    if (settings.temperature !== null) body['temperature'] = settings.temperature;
    if (settings.topP !== null) body['top_p'] = settings.topP;
    if (settings.reasoningEffort) body['reasoning_effort'] = settings.reasoningEffort;
    const response = await this.fetch(settings, 'chat/completions', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(body),
    });
    const envelope = await response.json() as { choices?: Array<{ message?: { content?: string } }> };
    const content = envelope.choices?.[0]?.message?.content;
    if (!content) throw new Error('The provider returned no response content.');
    try { return JSON.parse(content.replace(/^```json\s*|\s*```$/g, '')); }
    catch { throw new Error('The provider did not return valid JSON.'); }
  }

  private definitionSchema(): JsonSchema {
    return this.objectSchema({
      refinedStoryPrompt: { type: 'string' },
      suggestedTitle: { type: 'string' },
      initialEventsPrompt: { type: 'string' },
      initialStoryBibleEntries: {
        type: 'array',
        maxItems: 2000,
        items: this.proposedEntrySchema(),
      },
      initialPlannedEvents: {
        type: 'array',
        maxItems: 500,
        items: this.proposedPlannedEventSchema(),
      },
      initialVictoryConditions: {
        type: 'array',
        maxItems: 50,
        items: this.proposedConditionSchema(),
      },
      initialLossConditions: {
        type: 'array',
        maxItems: 50,
        items: this.proposedConditionSchema(),
      },
    });
  }

  private turnSchema(settings: AppSettings): JsonSchema {
    return this.objectSchema({
      turnNumber: { type: 'integer', minimum: 0 },
      acknowledgedPlayerAction: { type: ['string', 'null'] },
      narration: { type: 'string' },
      suggestedActions: {
        type: 'array',
        minItems: settings.minSuggestedActions,
        maxItems: settings.maxSuggestedActions,
        items: { type: 'string' },
      },
      relevantStoryBibleEntryIds: {
        type: 'array',
        items: { type: 'string', format: 'uuid' },
      },
      storyBibleUpdates: {
        type: 'array',
        items: this.objectSchema({
          operation: { type: 'string', enum: ['add', 'replace', 'remove'] },
          entryId: { type: ['string', 'null'] },
          entry: { anyOf: [this.proposedEntrySchema(), { type: 'null' }] },
        }),
      },
      relevantPlannedEventIds: {
        type: 'array',
        items: { type: 'string', format: 'uuid' },
      },
      plannedEventUpdates: {
        type: 'array',
        items: this.objectSchema({
          operation: { type: 'string', enum: ['add', 'replace', 'remove'] },
          entryId: { type: ['string', 'null'] },
          entry: { anyOf: [this.proposedPlannedEventSchema(), { type: 'null' }] },
          outcome: { type: ['string', 'null'], enum: ['fulfilled', 'abandoned', null] },
        }),
      },
      revealedVictoryConditionIds: { type: 'array', items: { type: 'string', format: 'uuid' } },
      metVictoryConditionIds: { type: 'array', items: { type: 'string', format: 'uuid' } },
      revealedLossConditionIds: { type: 'array', items: { type: 'string', format: 'uuid' } },
      metLossConditionIds: { type: 'array', items: { type: 'string', format: 'uuid' } },
    });
  }

  // condition is freeform prose describing what must happen, or what state the story must be in, before
  // this event can be pursued - not a structured reference to another entry. Nullable: most events have
  // no prerequisite. See the ProposedPlannedEvent comment in models.ts.
  private proposedPlannedEventSchema(): JsonSchema {
    return this.objectSchema({
      description: { type: 'string' },
      importance: { type: 'integer', minimum: 1, maximum: 5 },
      urgency: { type: 'integer', minimum: 1, maximum: 5 },
      condition: { type: ['string', 'null'] },
    });
  }

  private proposedEntrySchema(): JsonSchema {
    return this.objectSchema({
      category: { type: 'string' },
      name: { type: 'string' },
      knownFacts: { type: 'array', items: { type: 'string' } },
      secretFacts: { type: 'array', items: { type: 'string' } },
      importance: { type: 'integer', minimum: 1, maximum: 5 },
    });
  }

  private proposedConditionSchema(): JsonSchema {
    return this.objectSchema({
      description: { type: 'string' },
      secret: { type: 'boolean' },
    });
  }

  private objectSchema(properties: Record<string, unknown>): JsonSchema {
    return {
      type: 'object',
      additionalProperties: false,
      properties,
      required: Object.keys(properties),
    };
  }

  private resolutionRoll(): number {
    const values = new Uint32Array(1);
    const firstRejectedValue = 0x1_0000_0000 - (0x1_0000_0000 % 100);
    do {
      crypto.getRandomValues(values);
    } while (values[0] >= firstRejectedValue);
    return (values[0] % 100) + 1;
  }

  private async fetch(settings: AppSettings, relative: string, init: RequestInit): Promise<Response> {
    const controller = new AbortController();
    const timer = window.setTimeout(() => controller.abort(), settings.requestTimeoutSeconds * 1000);
    const base = settings.baseUrl.replace(/\/+$/, '');
    const headers = new Headers(init.headers);
    if (settings.apiKey) headers.set('Authorization', `Bearer ${settings.apiKey}`);
    try {
      const response = await fetch(`${base}/${relative}`, { ...init, headers, signal: controller.signal });
      if (!response.ok) {
        const text = (await response.text()).slice(0, 500);
        throw new ProviderHttpError(response.status, `Provider request failed (${response.status}): ${text || response.statusText}`);
      }
      return response;
    } catch (error) {
      if (error instanceof TypeError) throw new Error('The provider could not be reached. Check the URL and whether it permits browser CORS requests.');
      throw error;
    } finally {
      clearTimeout(timer);
    }
  }
}
