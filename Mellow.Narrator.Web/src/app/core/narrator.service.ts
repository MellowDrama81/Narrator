import { Injectable } from '@angular/core';
import { DbService } from './db.service';
import { LlmService } from './llm.service';
import {
  nowIso, StoryBibleEntry, StoryBibleUpdate, StoryDefinition, StoryState, StoryTurn, TrashItem, uuid,
} from './models';
import { applyPlannedEvents, resolveInitialPlannedEvents } from './planned-events';
import { applyConditionTurn, resolveInitialConditions } from './story-conditions';

@Injectable({ providedIn: 'root' })
export class NarratorService {
  private readonly activeStories = new Set<string>();

  constructor(private readonly db: DbService, private readonly llm: LlmService) {}

  async generateDefinition(title: string, prompt: string): Promise<StoryDefinition> {
    const settings = await this.db.settings();
    const generated = await this.llm.generateDefinition(settings, prompt);
    const definitions = await this.db.definitions();
    const now = nowIso();
    const value: StoryDefinition = {
      id: uuid(),
      title: title.trim() || generated.suggestedTitle.trim(),
      storyPrompt: generated.refinedStoryPrompt.trim(),
      initialEventsPrompt: generated.initialEventsPrompt.trim(),
      initialStoryBible: generated.initialStoryBibleEntries.map(entry => ({
        ...entry, id: uuid(), lastRelevantTurnNumber: 0,
      })),
      initialPlannedEvents: resolveInitialPlannedEvents(generated.initialPlannedEvents),
      initialVictoryConditions: resolveInitialConditions(generated.initialVictoryConditions),
      initialLossConditions: resolveInitialConditions(generated.initialLossConditions),
      sortOrder: definitions.length ? Math.max(...definitions.map(x => x.sortOrder)) + 1 : 0,
      createdAtUtc: now,
      updatedAtUtc: now,
    };
    await this.db.saveDefinition(value);
    return value;
  }

  async startStory(definition: StoryDefinition): Promise<StoryState> {
    const settings = await this.db.settings();
    const generated = await this.llm.opening(settings, definition);
    const stories = await this.db.stories();
    const id = uuid();
    const bible = this.applyBible(definition.initialStoryBible, generated.storyBibleUpdates, generated.relevantStoryBibleEntryIds, 0, settings.maxStoryBibleEntries);
    const plannedEvents = applyPlannedEvents(
      definition.initialPlannedEvents, generated.plannedEventUpdates, generated.relevantPlannedEventIds, 0, settings.maxPlannedEvents,
    );
    // Starting from empty revealed/met arrays, so this opening turn's delta and the story's new
    // cumulative totals are the same values.
    const victory = applyConditionTurn(
      definition.initialVictoryConditions, [], [], generated.revealedVictoryConditionIds, generated.metVictoryConditionIds,
    );
    const loss = applyConditionTurn(
      definition.initialLossConditions, [], [], generated.revealedLossConditionIds, generated.metLossConditionIds,
    );
    const now = nowIso();
    const turn: StoryTurn = {
      id: uuid(), storyStateId: id, sequenceNumber: 0, playerAction: null,
      narration: generated.narration, suggestedActions: generated.suggestedActions,
      relevantStoryBibleEntryIds: generated.relevantStoryBibleEntryIds,
      storyBibleUpdates: generated.storyBibleUpdates,
      relevantPlannedEventIds: generated.relevantPlannedEventIds,
      plannedEventUpdates: generated.plannedEventUpdates,
      revealedVictoryConditionIds: victory.revealed, metVictoryConditionIds: victory.met,
      revealedLossConditionIds: loss.revealed, metLossConditionIds: loss.met,
      completedAtUtc: now, modelId: settings.modelId,
    };
    const story: StoryState = {
      id, label: definition.title, sourceStoryDefinitionId: definition.id,
      definition: {
        title: definition.title,
        storyPrompt: definition.storyPrompt,
        initialEventsPrompt: definition.initialEventsPrompt,
        initialStoryBible: structuredClone(definition.initialStoryBible),
        initialPlannedEvents: structuredClone(definition.initialPlannedEvents),
        initialVictoryConditions: structuredClone(definition.initialVictoryConditions),
        initialLossConditions: structuredClone(definition.initialLossConditions),
      },
      currentStoryBible: bible,
      currentPlannedEvents: plannedEvents,
      currentVictoryConditions: structuredClone(definition.initialVictoryConditions),
      currentLossConditions: structuredClone(definition.initialLossConditions),
      revealedVictoryConditionIds: victory.revealed, metVictoryConditionIds: victory.met,
      revealedLossConditionIds: loss.revealed, metLossConditionIds: loss.met,
      sortOrder: stories.length ? Math.max(...stories.map(x => x.sortOrder)) + 1 : 0,
      startedAtUtc: now, lastActionAtUtc: null, turns: [turn],
    };
    await this.db.saveStory(story);
    return story;
  }

  async play(storyId: string, action: string): Promise<StoryState> {
    if (this.activeStories.has(storyId)) throw new Error('A request is already running for this story.');
    this.activeStories.add(storyId);
    try {
      const story = await this.db.story(storyId);
      if (!story) throw new Error('Story not found.');
      const settings = await this.db.settings();
      const generated = await this.llm.turn(settings, story, action.trim());
      const sequence = story.turns.length ? story.turns[story.turns.length - 1].sequenceNumber + 1 : 0;
      const now = nowIso();
      const bible = this.applyBible(story.currentStoryBible, generated.storyBibleUpdates, generated.relevantStoryBibleEntryIds, sequence, settings.maxStoryBibleEntries);
      const plannedEvents = applyPlannedEvents(
        story.currentPlannedEvents, generated.plannedEventUpdates, generated.relevantPlannedEventIds, sequence, settings.maxPlannedEvents,
      );
      // The condition lists themselves never change during play - only these deltas against the
      // story's existing cumulative revealed/met arrays.
      const victory = applyConditionTurn(
        story.currentVictoryConditions, story.revealedVictoryConditionIds, story.metVictoryConditionIds,
        generated.revealedVictoryConditionIds, generated.metVictoryConditionIds,
      );
      const loss = applyConditionTurn(
        story.currentLossConditions, story.revealedLossConditionIds, story.metLossConditionIds,
        generated.revealedLossConditionIds, generated.metLossConditionIds,
      );
      const turn: StoryTurn = {
        id: uuid(), storyStateId: story.id, sequenceNumber: sequence, playerAction: action.trim(),
        narration: generated.narration, suggestedActions: generated.suggestedActions,
        relevantStoryBibleEntryIds: generated.relevantStoryBibleEntryIds,
        storyBibleUpdates: generated.storyBibleUpdates,
        relevantPlannedEventIds: generated.relevantPlannedEventIds,
        plannedEventUpdates: generated.plannedEventUpdates,
        revealedVictoryConditionIds: victory.revealed, metVictoryConditionIds: victory.met,
        revealedLossConditionIds: loss.revealed, metLossConditionIds: loss.met,
        completedAtUtc: now, modelId: settings.modelId,
      };
      const updated = {
        ...story,
        currentStoryBible: bible,
        currentPlannedEvents: plannedEvents,
        revealedVictoryConditionIds: [...story.revealedVictoryConditionIds, ...victory.revealed],
        metVictoryConditionIds: [...story.metVictoryConditionIds, ...victory.met],
        revealedLossConditionIds: [...story.revealedLossConditionIds, ...loss.revealed],
        metLossConditionIds: [...story.metLossConditionIds, ...loss.met],
        lastActionAtUtc: now,
        turns: [...story.turns, turn],
      };
      await this.db.saveStory(updated);
      return updated;
    } finally {
      this.activeStories.delete(storyId);
    }
  }

  async copyStory(story: StoryState): Promise<StoryState> {
    const stories = await this.db.stories();
    const id = uuid();
    const copy: StoryState = structuredClone(story);
    copy.id = id;
    copy.label = `${story.label} — Copy`;
    copy.sortOrder = stories.length ? Math.max(...stories.map(x => x.sortOrder)) + 1 : 0;
    copy.startedAtUtc = nowIso();
    copy.turns = copy.turns.map(turn => ({ ...turn, id: uuid(), storyStateId: id }));
    await this.db.saveStory(copy);
    return copy;
  }

  async trashDefinition(value: StoryDefinition): Promise<void> {
    await this.db.saveTrash(this.trashItem('definition', value.id, value.title, value));
    await this.db.deleteDefinition(value.id);
  }

  async trashStory(value: StoryState): Promise<void> {
    await this.db.saveTrash(this.trashItem('story', value.id, value.label, value));
    await this.db.deleteStory(value.id);
  }

  async restore(item: TrashItem): Promise<void> {
    if (item.type === 'definition') {
      const definition = structuredClone(item.payload as StoryDefinition);
      if (await this.db.definition(definition.id)) definition.id = uuid();
      await this.db.saveDefinition(definition);
    } else {
      const story = structuredClone(item.payload as StoryState);
      if (await this.db.story(story.id)) {
        const nextId = uuid();
        story.id = nextId;
        story.turns = story.turns.map(turn => ({ ...turn, id: uuid(), storyStateId: nextId }));
      }
      await this.db.saveStory(story);
    }
    await this.db.deleteTrash(item.trashId);
  }

  applyBible(original: StoryBibleEntry[], updates: StoryBibleUpdate[], relevantIds: string[], sequence: number, maxEntries: number): StoryBibleEntry[] {
    const values = structuredClone(original);
    const relevant = new Set(relevantIds);
    for (const entry of values) if (relevant.has(entry.id)) entry.lastRelevantTurnNumber = sequence;
    for (const update of updates) {
      const index = update.entryId ? values.findIndex(x => x.id === update.entryId) : -1;
      if (update.operation === 'remove' && index >= 0) values.splice(index, 1);
      if (update.operation === 'replace' && index >= 0 && update.entry)
        values[index] = { ...update.entry, id: values[index].id, lastRelevantTurnNumber: sequence };
      if (update.operation === 'add' && update.entry)
        values.push({ ...update.entry, id: uuid(), lastRelevantTurnNumber: sequence });
    }
    return values
      .sort((a, b) => b.importance - a.importance || b.lastRelevantTurnNumber - a.lastRelevantTurnNumber)
      .slice(0, maxEntries);
  }

  private trashItem(type: TrashItem['type'], originalId: string, displayName: string, payload: TrashItem['payload']): TrashItem {
    return { trashId: uuid(), type, originalId, displayName, deletedAtUtc: nowIso(), payload: structuredClone(payload) };
  }
}

