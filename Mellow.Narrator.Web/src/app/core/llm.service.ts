import { Injectable } from '@angular/core';
import { DbService } from './db.service';
import {
  AppSettings, DefinitionGeneration, InstructionMessageRole, OutputTokenParameter, PlannedEventUpdate,
  StoryBibleUpdate, StoryCondition, StoryDefinition, StoryState, StructuredOutputTier, TurnGeneration,
} from './models';
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
type Message = { role: string; content: string };
interface RequestContract {
  outputTokenParameter: OutputTokenParameter;
  instructionMessageRole: InstructionMessageRole;
}

// A provider HTTP error after the retry/backoff loop in fetch() has given up (or found the status
// non-retryable) - message is already a friendly classification, see classifyHttpError. Treated as a
// capability-negotiation signal by completeStructured: worth trying the next structured-output
// tier/request contract candidate, mirroring OpenAiCompatibleProvider.TestConnectionAsync's
// `catch (Exception ex) when (ex is ProviderException or JsonException)`.
class ProviderHttpError extends Error {
  constructor(readonly status: number, message: string) {
    super(message);
  }
}

// The request never reached the provider at all (network unreachable, CORS, or timed out) - never worth
// retrying against a different structured-output tier or request contract, since the problem is
// transport-level, not shape-level. Mirrors C#'s HttpRequestException/TaskCanceledException, which
// propagate out of TestConnectionAsync's per-candidate try/catch rather than being swallowed by it.
class ProviderNetworkError extends Error {}

// Preference order for the structured-output tier: most capable (and most reliable, when supported)
// first. Mirrors the StrictJsonSchema -> JsonMode -> PromptedJson ordering used throughout
// OpenAiCompatibleProvider (RequestContractCandidates's inner loop, CompleteWithCorrectionAsync's
// PromptedJson fallback for an untested/unsupported tier).
const STRUCTURED_OUTPUT_TIER_ORDER: StructuredOutputTier[] = ['strictJsonSchema', 'jsonMode', 'promptedJson'];

// Mirrors OpenAiCompatibleProvider.RequestContractCandidates: preferring max_completion_tokens/developer
// first (the modern OpenAI-compatible contract), falling back progressively to the older max_tokens/
// system contract.
const REQUEST_CONTRACTS: RequestContract[] = [
  { outputTokenParameter: 'maxCompletionTokens', instructionMessageRole: 'developer' },
  { outputTokenParameter: 'maxCompletionTokens', instructionMessageRole: 'system' },
  { outputTokenParameter: 'maxTokens', instructionMessageRole: 'developer' },
  { outputTokenParameter: 'maxTokens', instructionMessageRole: 'system' },
];

function tierCandidates(current: StructuredOutputTier): StructuredOutputTier[] {
  const index = STRUCTURED_OUTPUT_TIER_ORDER.indexOf(current);
  return index >= 0 ? STRUCTURED_OUTPUT_TIER_ORDER.slice(index) : STRUCTURED_OUTPUT_TIER_ORDER;
}

function contractCandidates(settings: AppSettings): RequestContract[] {
  const index = REQUEST_CONTRACTS.findIndex(contract =>
    contract.outputTokenParameter === settings.outputTokenParameter &&
    contract.instructionMessageRole === settings.instructionMessageRole);
  return index >= 0 ? REQUEST_CONTRACTS.slice(index) : REQUEST_CONTRACTS;
}

// Mirrors NormalizedWords: splits on whitespace, strips everything but letters/digits from each word,
// lowercases, and drops anything that goes empty as a result.
function normalizedWords(value: string): string[] {
  return value
    .split(/\s+/)
    .slice(0, 4096)
    .map(word => word.toLowerCase().replace(/[^a-z0-9]/g, ''))
    .filter(word => word.length > 0);
}

function wordsEqual(a: string, b: string): boolean {
  const wordsA = normalizedWords(a);
  const wordsB = normalizedWords(b);
  return wordsA.length === wordsB.length && wordsA.every((word, index) => word === wordsB[index]);
}

// Mirrors Shingles: every consecutive run of 5 normalized words, deduplicated.
function shingles(words: string[]): Set<string> {
  const size = 5;
  const result = new Set<string>();
  for (let start = 0; start <= words.length - size; start++) result.add(words.slice(start, start + size).join(' '));
  return result;
}

// Mirrors IsSubstantiallyDuplicate exactly: an identical (after normalization) narration is always a
// duplicate; below 20 words there aren't enough 5-word shingles to judge reliably, so only an exact match
// is rejected; otherwise a candidate that shares at least 90% of the smaller shingle set's members and at
// least 80% of the combined (Jaccard) shingle set is rejected as a near-duplicate.
function isSubstantiallyDuplicate(candidate: string, previous: string): boolean {
  const candidateWords = normalizedWords(candidate);
  const previousWords = normalizedWords(previous);
  if (candidateWords.length === previousWords.length && candidateWords.every((word, index) => word === previousWords[index])) return true;
  if (candidateWords.length < 20 || previousWords.length < 20) return false;

  const candidateShingles = shingles(candidateWords);
  const previousShingles = shingles(previousWords);
  let intersection = 0;
  for (const shingle of candidateShingles) if (previousShingles.has(shingle)) intersection++;
  const smaller = Math.min(candidateShingles.size, previousShingles.size);
  const union = candidateShingles.size + previousShingles.size - intersection;
  return smaller > 0 && intersection / smaller >= 0.9 && intersection / union >= 0.8;
}

// Mirrors ExampleFor: generically walks any of our JSON Schemas to synthesize a structurally-correct
// (not semantically meaningful) example instance, for the PromptedJson fallback tier's instruction - weak
// models tend to follow a concrete example far more reliably than raw schema syntax.
function exampleFor(schema: unknown): unknown {
  if (!schema || typeof schema !== 'object') return null;
  const obj = schema as Record<string, unknown>;
  if (Array.isArray(obj['enum']) && (obj['enum'] as unknown[]).length > 0) return (obj['enum'] as unknown[])[0];
  if (Array.isArray(obj['anyOf'])) {
    const options = obj['anyOf'] as Array<Record<string, unknown>>;
    const nonNull = options.find(option => option['type'] !== 'null');
    return exampleFor(nonNull ?? options[0]);
  }
  const rawType = obj['type'];
  const type = Array.isArray(rawType) ? (rawType as string[]).find(value => value !== 'null') : rawType;
  switch (type) {
    case 'object': return exampleObject(obj);
    case 'array': return [exampleFor(obj['items'])];
    case 'string': return obj['format'] === 'uuid' ? '00000000-0000-0000-0000-000000000000' : 'string';
    case 'integer': return typeof obj['minimum'] === 'number' ? obj['minimum'] : 0;
    case 'number': return typeof obj['minimum'] === 'number' ? obj['minimum'] : 0;
    case 'boolean': return true;
    default: return null;
  }
}

// Mirrors ExampleObject.
function exampleObject(schema: Record<string, unknown>): Record<string, unknown> {
  const result: Record<string, unknown> = {};
  const properties = schema['properties'];
  if (properties && typeof properties === 'object')
    for (const [key, value] of Object.entries(properties as Record<string, unknown>)) result[key] = exampleFor(value);
  return result;
}

@Injectable({ providedIn: 'root' })
export class LlmService {
  constructor(private readonly db: DbService) {}

  async loadModels(settings: AppSettings): Promise<string[]> {
    const response = await this.fetch(settings, 'models', { method: 'GET' });
    const body = await response.json() as { data?: Array<{ id?: string }> };
    return (body.data ?? []).map(x => x.id ?? '').filter(Boolean).sort();
  }

  async test(settings: AppSettings): Promise<string> {
    if (!settings.baseUrl) throw new Error('Configure an API base URL first.');
    if (!settings.modelId) throw new Error('Choose or enter a model first.');
    const { value } = await this.completeStructured(settings, [
      { role: 'system', content: 'Return a JSON object with exactly one boolean property named ok.' },
      { role: 'user', content: 'Return ok as true.' },
    ], this.objectSchema({ ok: { type: 'boolean' } }));
    if (value['ok'] !== true) throw new Error('The model could not produce a valid structured response.');
    return `Connected to ${settings.modelId}.`;
  }

  async generateDefinition(settings: AppSettings, storyPrompt: string): Promise<DefinitionGeneration> {
    return this.completeWithCorrection(settings, [
      { role: 'system', content: promptTemplates.storyDefinitionInstruction },
      { role: 'user', content: storyPrompt },
    ], this.definitionSchema(settings), value => this.parseDefinition(value));
  }

  private parseDefinition(value: Record<string, unknown>): DefinitionGeneration {
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
    const messages: Message[] = [
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

    return this.completeWithCorrection(settings, messages, this.turnSchema(settings), value =>
      this.parseTurn(value, settings, next, action, turns, victory, loss));
  }

  private parseTurn(
    value: Record<string, unknown>,
    settings: AppSettings,
    next: number,
    action: string | null,
    turns: StoryState['turns'],
    victory: ConditionsContext,
    loss: ConditionsContext,
  ): TurnGeneration {
    const result = value as Partial<TurnGeneration> & Record<string, unknown>;

    if (typeof result.turnNumber !== 'number' || result.turnNumber !== next)
      throw new Error(`The response acknowledged turn ${String(result.turnNumber)}, but the current turn is ${next}.`);

    const acknowledged = result.acknowledgedPlayerAction;
    if (action === null) {
      if (acknowledged !== null && acknowledged !== undefined)
        throw new Error('An opening-scene response must acknowledge a null player action.');
    } else if (typeof acknowledged !== 'string' || !wordsEqual(acknowledged, action)) {
      throw new Error('The response acknowledged a different player action. Respond to currentPlayerAction and copy it exactly.');
    }

    if (typeof result.narration !== 'string' || !result.narration.trim() || result.narration.length > settings.maxNarrationCharacters)
      throw new Error('Narration is empty or exceeds the configured limit.');
    if (action !== null && turns.some(turn => isSubstantiallyDuplicate(result.narration as string, turn.narration)))
      throw new Error('The narration duplicates a recent scene. Advance the story by resolving currentPlayerAction instead.');

    if (!Array.isArray(result.suggestedActions) || result.suggestedActions.length === 0)
      throw new Error('The model returned an incomplete story turn.');
    for (const suggestion of result.suggestedActions) {
      if (typeof suggestion !== 'string' || !suggestion.trim() || suggestion.length > settings.maxSuggestedActionCharacters)
        throw new Error('A suggested action is empty or exceeds the configured limit.');
    }

    return {
      turnNumber: next,
      acknowledgedPlayerAction: action,
      narration: result.narration,
      suggestedActions: result.suggestedActions.map(String).filter(Boolean).slice(0, settings.maxSuggestedActions),
      relevantStoryBibleEntryIds: Array.isArray(result.relevantStoryBibleEntryIds) ? result.relevantStoryBibleEntryIds.map(String) : [],
      storyBibleUpdates: Array.isArray(result.storyBibleUpdates) ? result.storyBibleUpdates as StoryBibleUpdate[] : [],
      relevantPlannedEventIds: Array.isArray(result.relevantPlannedEventIds) ? result.relevantPlannedEventIds.map(String) : [],
      plannedEventUpdates: Array.isArray(result.plannedEventUpdates) ? result.plannedEventUpdates as PlannedEventUpdate[] : [],
      revealedVictoryConditionIds: normalizeConditionIds(
        Array.isArray(result.revealedVictoryConditionIds) ? result.revealedVictoryConditionIds : [],
        victory.conditions, victory.revealedIds, victory.metIds, true,
      ),
      metVictoryConditionIds: normalizeConditionIds(
        Array.isArray(result.metVictoryConditionIds) ? result.metVictoryConditionIds : [],
        victory.conditions, victory.revealedIds, victory.metIds, false,
      ),
      revealedLossConditionIds: normalizeConditionIds(
        Array.isArray(result.revealedLossConditionIds) ? result.revealedLossConditionIds : [],
        loss.conditions, loss.revealedIds, loss.metIds, true,
      ),
      metLossConditionIds: normalizeConditionIds(
        Array.isArray(result.metLossConditionIds) ? result.metLossConditionIds : [],
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

  // Wraps completeStructured with a single corrective retry on a validation failure - mirrors
  // CompleteWithCorrectionAsync: the retry reuses the exact structured-output tier and request contract
  // that the first attempt actually succeeded with (never re-probes those), appends
  // CorrectiveRetryInstruction describing what went wrong, and gives the model exactly one more attempt.
  // If the corrected attempt also fails validation, that failure propagates to the caller as-is.
  private async completeWithCorrection<T>(
    settings: AppSettings,
    messages: Message[],
    schema: JsonSchema,
    parseAndValidate: (value: Record<string, unknown>) => T,
  ): Promise<T> {
    const { value, tier } = await this.completeStructured(settings, messages, schema);
    try {
      return parseAndValidate(value);
    } catch (error) {
      if (error instanceof ProviderNetworkError) throw error;
      const validationError = error instanceof Error ? error.message : String(error);
      const corrected = [...messages, {
        role: 'system',
        content: promptTemplates.correctiveRetryInstruction.replace('{validationError}', validationError),
      }];
      const { value: correctedValue } = await this.completeStructured(settings, corrected, schema, tier);
      return parseAndValidate(correctedValue);
    }
  }

  // Negotiates a working (structured-output tier, request contract) combination and returns the raw
  // parsed JSON object. Starts from whatever settings already records (skipping combinations already
  // proven not to work) and, on a failure that looks like an unsupported-parameter/response-format
  // problem - a 400/404/422 HTTP status, or a 200 response whose body isn't usable JSON - steps down to
  // the next-most-conservative combination - contract outermost, tier innermost, mirroring
  // TestConnectionAsync's nested RequestContractCandidates/tier loops. Any other HTTP failure (401/403,
  // or a 429/5xx that fetch()'s own transient-failure retry loop already exhausted) is not a
  // capability-shape problem, so it propagates immediately instead of being retried against every
  // remaining candidate. Once any combination succeeds, it's persisted to settings via db.saveSettings so
  // later calls skip straight to it, instead of the old in-memory-only Map. Unlike C#, this probing
  // happens during real generation, not only from an explicit "Test connection" action - there is no
  // equivalent explicit step in this app, so it must learn the working combination from real traffic
  // instead.
  private async completeStructured(
    settings: AppSettings,
    messages: Message[],
    schema: JsonSchema,
    forcedTier?: StructuredOutputTier,
  ): Promise<{ value: Record<string, unknown>; tier: StructuredOutputTier }> {
    if (!settings.baseUrl) throw new Error('Configure an API base URL first.');
    if (!settings.modelId) throw new Error('Choose or enter a model first.');

    const tiers = forcedTier ? [forcedTier] : tierCandidates(settings.structuredOutputTier);
    const contracts = forcedTier
      ? [{ outputTokenParameter: settings.outputTokenParameter, instructionMessageRole: settings.instructionMessageRole }]
      : contractCandidates(settings);

    let lastError: unknown;
    for (const contract of contracts) {
      for (const tier of tiers) {
        try {
          const value = await this.requestCompletion(settings, messages, schema, tier, contract);
          if (settings.structuredOutputTier !== tier ||
              settings.outputTokenParameter !== contract.outputTokenParameter ||
              settings.instructionMessageRole !== contract.instructionMessageRole) {
            settings.structuredOutputTier = tier;
            settings.outputTokenParameter = contract.outputTokenParameter;
            settings.instructionMessageRole = contract.instructionMessageRole;
            await this.db.saveSettings(settings);
          }
          return { value, tier };
        } catch (error) {
          lastError = error;
          if (error instanceof ProviderNetworkError) throw error;
          if (error instanceof ProviderHttpError && ![400, 404, 422].includes(error.status)) throw error;
        }
      }
    }
    throw lastError instanceof Error ? lastError : new Error('The provider request failed.');
  }

  private async requestCompletion(
    settings: AppSettings,
    messages: Message[],
    schema: JsonSchema,
    tier: StructuredOutputTier,
    contract: RequestContract,
  ): Promise<Record<string, unknown>> {
    const requestMessages = tier === 'promptedJson'
      ? [...messages, { role: 'system', content: this.promptedJsonInstruction(schema) }]
      : messages;
    const instructionRole: string = contract.instructionMessageRole;
    const serializedMessages = requestMessages.map(message => ({
      ...message,
      role: message.role === 'system' ? instructionRole : message.role,
    }));

    const body: Record<string, unknown> = {
      model: settings.modelId,
      messages: serializedMessages,
      stream: false,
    };
    body[contract.outputTokenParameter === 'maxCompletionTokens' ? 'max_completion_tokens' : 'max_tokens'] = settings.maxOutputTokens;
    if (settings.temperature !== null) body['temperature'] = settings.temperature;
    if (settings.topP !== null) body['top_p'] = settings.topP;
    if (settings.reasoningEffort) body['reasoning_effort'] = settings.reasoningEffort;
    if (tier === 'strictJsonSchema')
      body['response_format'] = { type: 'json_schema', json_schema: { name: 'mellow_narrator_response', strict: true, schema } };
    else if (tier === 'jsonMode')
      body['response_format'] = { type: 'json_object' };

    const response = await this.fetch(settings, 'chat/completions', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(body),
    });
    const envelope = await response.json() as { choices?: Array<{ message?: { content?: string; refusal?: string } }> };
    const message = envelope.choices?.[0]?.message;
    if (message?.refusal) throw new Error('The model refused the request.');
    const content = message?.content;
    if (!content) throw new Error('The provider returned no response content.');
    let value: unknown;
    try { value = JSON.parse(content.replace(/^```json\s*|\s*```$/g, '')); }
    catch { throw new Error('The provider did not return valid JSON.'); }
    if (!value || typeof value !== 'object' || Array.isArray(value)) throw new Error('The model response is not a JSON object.');
    return value as Record<string, unknown>;
  }

  private promptedJsonInstruction(schema: JsonSchema): string {
    return promptTemplates.promptedJsonInstruction
      .replace('{schema}', JSON.stringify(schema))
      .replace('{example}', JSON.stringify(exampleFor(schema) ?? {}));
  }

  private definitionSchema(settings: AppSettings): JsonSchema {
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
        items: this.proposedPlannedEventSchema(settings),
      },
      initialVictoryConditions: {
        type: 'array',
        maxItems: 50,
        items: this.proposedConditionSchema(settings),
      },
      initialLossConditions: {
        type: 'array',
        maxItems: 50,
        items: this.proposedConditionSchema(settings),
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
          entry: { anyOf: [this.proposedPlannedEventSchema(settings), { type: 'null' }] },
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
  private proposedPlannedEventSchema(settings: AppSettings): JsonSchema {
    return this.objectSchema({
      description: { type: 'string', maxLength: settings.maxPlannedEventDescriptionCharacters },
      importance: { type: 'integer', minimum: 1, maximum: 5 },
      urgency: { type: 'integer', minimum: 1, maximum: 5 },
      condition: { type: ['string', 'null'], maxLength: settings.maxPlannedEventConditionCharacters },
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

  private proposedConditionSchema(settings: AppSettings): JsonSchema {
    return this.objectSchema({
      description: { type: 'string', maxLength: settings.maxConditionDescriptionCharacters },
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

  // Performs one HTTP request with automatic retry/backoff on a transient failure (429, request
  // timeout, or 5xx from the provider, or a network-level failure such as being unreachable) - mirrors
  // SendAsync/RetryDelay/Backoff. Up to settings.maxAutomaticRetries retries (maxAutomaticRetries + 1
  // total attempts), exponential backoff (InitialDelay * 2^attempt, capped at MaxDelay) with ~20%
  // jitter, honoring a Retry-After response header when present and within
  // settings.retryMaxRetryAfterSeconds. A non-retryable or exhausted failure throws an already-classified
  // ProviderHttpError (see classifyHttpError); a network failure or timeout throws ProviderNetworkError.
  private async fetch(settings: AppSettings, relative: string, init: RequestInit): Promise<Response> {
    if (!settings.baseUrl) throw new Error('Configure an API base URL first.');
    const base = settings.baseUrl.replace(/\/+$/, '');
    const headers = new Headers(init.headers);
    if (settings.apiKey) headers.set('Authorization', `Bearer ${settings.apiKey}`);
    const maxAttempts = settings.maxAutomaticRetries + 1;
    let lastNetworkError: unknown;

    for (let attempt = 0; attempt < maxAttempts; attempt++) {
      const controller = new AbortController();
      const timer = window.setTimeout(() => controller.abort(), settings.requestTimeoutSeconds * 1000);
      try {
        const response = await fetch(`${base}/${relative}`, { ...init, headers, signal: controller.signal });
        if (response.ok) return response;
        const text = (await response.text()).slice(0, 2000);
        const retryable = response.status === 429 || response.status === 408 || response.status >= 500;
        const error = this.classifyHttpError(response, text, settings.apiKey);
        if (retryable && attempt < maxAttempts - 1) {
          const delay = this.retryDelay(settings, response, attempt);
          if (delay === null) throw error;
          await this.wait(delay);
          continue;
        }
        throw error;
      } catch (error) {
        if (error instanceof ProviderHttpError) throw error;
        lastNetworkError = error;
        if (attempt >= maxAttempts - 1) break;
        await this.wait(this.backoff(settings, attempt));
      } finally {
        clearTimeout(timer);
      }
    }
    const timedOut = lastNetworkError instanceof DOMException && lastNetworkError.name === 'AbortError';
    throw new ProviderNetworkError(timedOut
      ? 'The provider request timed out.'
      : 'The provider could not be reached. Check the URL and whether it permits browser CORS requests.');
  }

  // Mirrors Error(): maps a non-success HTTP response into a friendly, specific message where the status
  // and body allow it - auth failure, model-not-found, context-length-exceeded (suggesting a smaller
  // recent-turn count), or a rejected configured parameter - falling back to the raw status and detail
  // text otherwise. The credential, if any, is redacted from the detail text before it reaches the UI.
  private classifyHttpError(response: Response, bodyText: string, apiKey: string): ProviderHttpError {
    let detail = bodyText || response.statusText || 'Provider error';
    try {
      const parsed = JSON.parse(bodyText) as { error?: { message?: unknown } };
      if (typeof parsed.error?.message === 'string' && parsed.error.message) detail = parsed.error.message;
    } catch { /* body is not JSON; use the raw text */ }
    if (apiKey) detail = detail.split(apiKey).join('[REDACTED CREDENTIAL]');

    const lower = detail.toLowerCase();
    const containsAny = (...terms: string[]) => terms.some(term => lower.includes(term));
    const status = response.status;
    let message: string;
    if (status === 401 || status === 403) message = `Authentication failed: ${detail}`;
    else if (status === 404) message = `API endpoint was not found: ${detail}`;
    else if (status === 400 && containsAny('temperature', 'top_p', 'reasoning_effort')) message = `The selected model rejected a configured parameter: ${detail}`;
    else if (status === 400 && containsAny('context length', 'context_length', 'maximum context')) message = `The provider rejected the request for context length. Reduce the recent-turn count: ${detail}`;
    else if (status === 400 && containsAny('model', 'not found')) message = `The selected model is unavailable: ${detail}`;
    else message = `${status} ${detail}`;
    return new ProviderHttpError(status, message);
  }

  // Mirrors RetryDelay: prefers a Retry-After response header (capped at retryMaxRetryAfterSeconds, or
  // treated as not retryable at all if it exceeds that), falling back to the computed backoff.
  private retryDelay(settings: AppSettings, response: Response, attempt: number): number | null {
    const header = response.headers.get('retry-after');
    if (header) {
      let ms: number | null = null;
      const seconds = Number(header);
      if (!Number.isNaN(seconds)) ms = seconds * 1000;
      else {
        const date = Date.parse(header);
        if (!Number.isNaN(date)) ms = date - Date.now();
      }
      if (ms !== null) {
        if (ms < 0) ms = 0;
        return ms <= settings.retryMaxRetryAfterSeconds * 1000 ? ms : null;
      }
    }
    return this.backoff(settings, attempt);
  }

  // Mirrors Backoff: InitialDelay * 2^attempt, capped at MaxDelay, plus up to 20% jitter.
  private backoff(settings: AppSettings, attempt: number): number {
    const raw = Math.min(settings.retryMaxDelaySeconds * 1000, settings.retryInitialDelaySeconds * 1000 * 2 ** attempt);
    return raw * (1 + Math.random() * 0.2);
  }

  private wait(ms: number): Promise<void> {
    return new Promise(resolve => setTimeout(resolve, ms));
  }
}
