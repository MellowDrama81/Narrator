import { Injectable } from '@angular/core';
import { AppSettings, DefinitionGeneration, StoryDefinition, StoryState, TurnGeneration } from './models';

const definitionInstruction = `You design durable interactive stories. Refine the user's premise into immutable setting, tone, and narration rules. Move mutable character, place, relationship, inventory, and objective facts into Story Bible entries. Each entry has category, name, knownFacts, secretFacts, and importance from 1 to 5. Return JSON only with refinedStoryPrompt, suggestedTitle, initialEventsPrompt, and initialStoryBibleEntries.`;

const narrationInstruction = `You narrate an interactive story in second-person present tense. The Story Bible is authoritative. Secret facts must not be revealed until story events make the player character aware of them. Advance the scene, resolve the player's attempted action plausibly, stop at the next meaningful decision, and return JSON only. Return turnNumber, acknowledgedPlayerAction, narration, suggestedActions, relevantStoryBibleEntryIds, and incremental storyBibleUpdates. Each update is add, replace, or remove; add uses a null entryId, while replace/remove use an existing ID.`;

@Injectable({ providedIn: 'root' })
export class LlmService {
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
    ]);
    return `Connected to ${settings.modelId}.`;
  }

  async generateDefinition(settings: AppSettings, storyPrompt: string): Promise<DefinitionGeneration> {
    const value = await this.complete(settings, [
      { role: 'system', content: definitionInstruction },
      { role: 'user', content: storyPrompt },
    ]);
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
    };
  }

  opening(settings: AppSettings, definition: StoryDefinition): Promise<TurnGeneration> {
    return this.generateTurn(settings, definition, [], definition.initialStoryBible, null);
  }

  turn(settings: AppSettings, story: StoryState, action: string): Promise<TurnGeneration> {
    return this.generateTurn(
      settings,
      story.definition,
      story.turns.slice(-settings.recentTurnCount),
      story.currentStoryBible,
      action,
    );
  }

  private async generateTurn(
    settings: AppSettings,
    definition: Pick<StoryDefinition, 'storyPrompt' | 'initialEventsPrompt'>,
    turns: StoryState['turns'],
    bible: StoryState['currentStoryBible'],
    action: string | null,
  ): Promise<TurnGeneration> {
    const next = turns.length ? turns[turns.length - 1].sequenceNumber + 1 : 0;
    const context = {
      storyPrompt: definition.storyPrompt,
      initialEventsPrompt: turns.length < settings.recentTurnCount ? definition.initialEventsPrompt : undefined,
      storyBible: bible,
      recentTurns: turns.map(x => ({ action: x.playerAction, narration: x.narration })),
      playerAction: action,
      turnNumber: next,
      requestedFormat: {
        paragraphs: `${settings.minParagraphs}-${settings.maxParagraphs}`,
        suggestedActions: `${settings.minSuggestedActions}-${settings.maxSuggestedActions}`,
      },
    };
    const value = await this.complete(settings, [
      { role: 'system', content: narrationInstruction },
      { role: 'user', content: JSON.stringify(context) },
    ]) as Partial<TurnGeneration>;
    if (!value.narration || !Array.isArray(value.suggestedActions))
      throw new Error('The model returned an incomplete story turn.');
    return {
      turnNumber: next,
      acknowledgedPlayerAction: action,
      narration: value.narration,
      suggestedActions: value.suggestedActions.map(String).filter(Boolean).slice(0, settings.maxSuggestedActions),
      relevantStoryBibleEntryIds: Array.isArray(value.relevantStoryBibleEntryIds) ? value.relevantStoryBibleEntryIds.map(String) : [],
      storyBibleUpdates: Array.isArray(value.storyBibleUpdates) ? value.storyBibleUpdates : [],
    };
  }

  private async complete(settings: AppSettings, messages: Array<{ role: string; content: string }>): Promise<unknown> {
    if (!settings.baseUrl) throw new Error('Configure an API base URL first.');
    if (!settings.modelId) throw new Error('Choose or enter a model first.');
    const body: Record<string, unknown> = {
      model: settings.modelId,
      messages,
      max_tokens: settings.maxOutputTokens,
      response_format: { type: 'json_object' },
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
        throw new Error(`Provider request failed (${response.status}): ${text || response.statusText}`);
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

