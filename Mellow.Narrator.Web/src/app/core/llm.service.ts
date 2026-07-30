import { Injectable } from '@angular/core';
import { AppSettings, DefinitionGeneration, StoryDefinition, StoryState, TurnGeneration } from './models';
import { promptTemplates } from './prompt-templates.generated';

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
      { role: 'system', content: promptTemplates.storyDefinitionInstruction },
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
      { role: 'system', content: this.renderNarrationInstruction(settings, turns.length === 0) },
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

  private renderNarrationInstruction(settings: AppSettings, opening: boolean): string {
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

    const phase = opening
      ? `${promptTemplates.openingSceneInstruction} Copy turnNumber exactly into the response and set acknowledgedPlayerAction to null.`
      : `${promptTemplates.continueStoryInstruction} Resolve currentPlayerAction now and copy it exactly into acknowledgedPlayerAction.`;
    return `${instruction}\n\n${phase}`;
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
