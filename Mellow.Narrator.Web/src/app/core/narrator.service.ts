import { Injectable } from '@angular/core';
import { DbService } from './db.service';
import { LlmService } from './llm.service';
import {
  AppSettings, nowIso, StoryDefinition, StoryState, StoryTurn, TrashItem, uuid,
} from './models';
import {
  applyPlannedEvents, cullPlannedEventsToLimits, isWithinPlannedEventLimits, PlannedEventLimits,
  resolveInitialPlannedEvents,
} from './planned-events';
import { applyStoryBible, cullBibleToLimits, isWithinBibleLimits, StoryBibleLimits } from './story-bible';
import { applyConditionTurn, ConditionLimits, isWithinConditionLimits, resolveInitialConditions } from './story-conditions';

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
      initialPlannedEvents: resolveInitialPlannedEvents(generated.initialPlannedEvents, this.plannedEventLimits(settings)),
      initialVictoryConditions: resolveInitialConditions(generated.initialVictoryConditions, this.conditionLimits(settings)),
      initialLossConditions: resolveInitialConditions(generated.initialLossConditions, this.conditionLimits(settings)),
      sortOrder: definitions.length ? Math.max(...definitions.map(x => x.sortOrder)) + 1 : 0,
      createdAtUtc: now,
      updatedAtUtc: now,
    };
    await this.db.saveDefinition(value);
    return value;
  }

  async startStory(definition: StoryDefinition): Promise<StoryState> {
    const settings = await this.db.settings();
    const bibleLimits = this.bibleLimits(settings);
    const plannedEventLimits = this.plannedEventLimits(settings);
    const conditionLimits = this.conditionLimits(settings);
    // Pre-flight limit checks BEFORE ever calling the provider - mirrors
    // NarratorApplication.StartStoryAsync, which refuses to spend a request on content the user has
    // already made too big for the currently configured limits (e.g. by lowering a limit setting after
    // the Definition was authored).
    if (!isWithinBibleLimits(definition.initialStoryBible, bibleLimits))
      throw new Error('The initial Story Bible exceeds current limits. Increase the limits or cull it first.');
    if (!isWithinPlannedEventLimits(definition.initialPlannedEvents, plannedEventLimits))
      throw new Error('The initial Planned Events exceed current limits. Increase the limits or cull them first.');
    if (!isWithinConditionLimits(definition.initialVictoryConditions, conditionLimits))
      throw new Error('The initial Victory Conditions exceed current limits.');
    if (!isWithinConditionLimits(definition.initialLossConditions, conditionLimits))
      throw new Error('The initial Loss Conditions exceed current limits.');

    // Remap every Story Bible entry / Planned Event / Condition id to a fresh uuid before this
    // playthrough begins, via one shared old-id -> new-id map, so multiple playthroughs started from the
    // same Definition never share ids - mirrors NarratorApplication.StartStoryAsync's idMap/MapId (ids
    // never collide across the four lists, so one shared map safely covers all of them). This must happen
    // BEFORE the opening request is built, since the request sends these ids to the model and the
    // response's relevantStoryBibleEntryIds/etc. must be interpreted against the same remapped ids.
    const idMap = new Map<string, string>();
    const mapId = (oldId: string): string => {
      const existing = idMap.get(oldId);
      if (existing) return existing;
      const mapped = uuid();
      idMap.set(oldId, mapped);
      return mapped;
    };
    const remapped: StoryDefinition = {
      ...definition,
      initialStoryBible: definition.initialStoryBible.map(entry => ({ ...entry, id: mapId(entry.id), lastRelevantTurnNumber: 0 })),
      initialPlannedEvents: definition.initialPlannedEvents.map(entry => ({ ...entry, id: mapId(entry.id), lastRelevantTurnNumber: 0 })),
      initialVictoryConditions: definition.initialVictoryConditions.map(entry => ({ ...entry, id: mapId(entry.id) })),
      initialLossConditions: definition.initialLossConditions.map(entry => ({ ...entry, id: mapId(entry.id) })),
    };

    const generated = await this.llm.opening(settings, remapped);
    const stories = await this.db.stories();
    const id = uuid();
    const bible = applyStoryBible(
      remapped.initialStoryBible, generated.storyBibleUpdates, generated.relevantStoryBibleEntryIds, 0, bibleLimits,
    );
    const plannedEvents = applyPlannedEvents(
      remapped.initialPlannedEvents, generated.plannedEventUpdates, generated.relevantPlannedEventIds, 0, plannedEventLimits,
    );
    // Starting from empty revealed/met arrays, so this opening turn's delta and the story's new
    // cumulative totals are the same values.
    const victory = applyConditionTurn(
      remapped.initialVictoryConditions, [], [], generated.revealedVictoryConditionIds, generated.metVictoryConditionIds,
    );
    const loss = applyConditionTurn(
      remapped.initialLossConditions, [], [], generated.revealedLossConditionIds, generated.metLossConditionIds,
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
        initialStoryBible: structuredClone(remapped.initialStoryBible),
        initialPlannedEvents: structuredClone(remapped.initialPlannedEvents),
        initialVictoryConditions: structuredClone(remapped.initialVictoryConditions),
        initialLossConditions: structuredClone(remapped.initialLossConditions),
      },
      currentStoryBible: bible,
      currentPlannedEvents: plannedEvents,
      currentVictoryConditions: structuredClone(remapped.initialVictoryConditions),
      currentLossConditions: structuredClone(remapped.initialLossConditions),
      revealedVictoryConditionIds: victory.revealed, metVictoryConditionIds: victory.met,
      revealedLossConditionIds: loss.revealed, metLossConditionIds: loss.met,
      storySummary: generated.storySummary,
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
      // Pre-flight limit checks BEFORE ever calling the provider - mirrors
      // NarratorApplication.PlayTurnAsync, which refuses to spend a request on content the user has
      // already made too big for the currently configured limits (e.g. by lowering a limit setting after
      // earlier turns grew the Story Bible/Planned Events). Conditions have no equivalent check here,
      // matching PlayTurnAsync - the condition lists never change during play.
      if (!isWithinBibleLimits(story.currentStoryBible, this.bibleLimits(settings)))
        throw new Error('The Story Bible exceeds current limits. Increase the limits or cull it first.');
      if (!isWithinPlannedEventLimits(story.currentPlannedEvents, this.plannedEventLimits(settings)))
        throw new Error('The Planned Events exceed current limits. Increase the limits or cull them first.');
      const generated = await this.llm.turn(settings, story, action.trim());
      const sequence = story.turns.length ? story.turns[story.turns.length - 1].sequenceNumber + 1 : 0;
      const now = nowIso();
      const bible = applyStoryBible(
        story.currentStoryBible, generated.storyBibleUpdates, generated.relevantStoryBibleEntryIds, sequence, this.bibleLimits(settings),
      );
      const plannedEvents = applyPlannedEvents(
        story.currentPlannedEvents, generated.plannedEventUpdates, generated.relevantPlannedEventIds, sequence, this.plannedEventLimits(settings),
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
        storySummary: generated.storySummary,
        lastActionAtUtc: now,
        turns: [...story.turns, turn],
      };
      await this.db.saveStory(updated);
      return updated;
    } finally {
      this.activeStories.delete(storyId);
    }
  }

  // Manual-edit escape hatch for a plain scalar - mirrors NarratorApplication.UpdateStorySummaryAsync:
  // trim, length-validate against the currently configured limit, save, return the updated story. No
  // maintenance/history tracking, unlike the Story Bible/Planned Events manual edits, since this is a
  // single narrator-rewritten string rather than a diffable collection of entries.
  async updateStorySummary(stateId: string, summary: string): Promise<StoryState> {
    const story = await this.db.story(stateId);
    if (!story) throw new Error('Story not found.');
    const settings = await this.db.settings();
    const trimmed = summary.trim();
    if (trimmed.length > settings.maxStorySummaryCharacters)
      throw new Error('The story summary exceeds the configured limit.');
    const updated: StoryState = { ...story, storySummary: trimmed };
    await this.db.saveStory(updated);
    return updated;
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

  // User-triggered "cull to limits" action for a Story Definition's initial Story Bible/Planned
  // Events - mirrors NarratorApplication.CullDefinitionAsync. Conditions have no cull mechanism, by
  // design, matching the C# side.
  async cullDefinition(definition: StoryDefinition): Promise<StoryDefinition> {
    const settings = await this.db.settings();
    const bible = cullBibleToLimits(definition.initialStoryBible, this.bibleLimits(settings));
    const plannedEvents = cullPlannedEventsToLimits(definition.initialPlannedEvents, this.plannedEventLimits(settings));
    const updated: StoryDefinition = {
      ...definition,
      initialStoryBible: bible.entries,
      initialPlannedEvents: plannedEvents.entries,
      updatedAtUtc: nowIso(),
    };
    await this.db.saveDefinition(updated);
    return updated;
  }

  // User-triggered "cull to limits" action for a Story State's current Story Bible/Planned Events -
  // mirrors NarratorApplication.CullStoryStateAsync. Conditions have no cull mechanism, by design,
  // matching the C# side.
  async cullStoryState(state: StoryState): Promise<StoryState> {
    const settings = await this.db.settings();
    const bible = cullBibleToLimits(state.currentStoryBible, this.bibleLimits(settings));
    const plannedEvents = cullPlannedEventsToLimits(state.currentPlannedEvents, this.plannedEventLimits(settings));
    const updated: StoryState = {
      ...state,
      currentStoryBible: bible.entries,
      currentPlannedEvents: plannedEvents.entries,
    };
    await this.db.saveStory(updated);
    return updated;
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

  private bibleLimits(settings: AppSettings): StoryBibleLimits {
    return {
      maxEntries: settings.maxStoryBibleEntries,
      maxEntryCharacters: settings.maxStoryBibleEntryCharacters,
      maxTotalCharacters: settings.maxStoryBibleCharacters,
    };
  }

  private plannedEventLimits(settings: AppSettings): PlannedEventLimits {
    return {
      maxEntries: settings.maxPlannedEvents,
      maxEntryCharacters: settings.maxPlannedEventCharacters,
      maxTotalCharacters: settings.maxPlannedEventsCharacters,
      maxDescriptionCharacters: settings.maxPlannedEventDescriptionCharacters,
      maxConditionCharacters: settings.maxPlannedEventConditionCharacters,
    };
  }

  private conditionLimits(settings: AppSettings): ConditionLimits {
    return {
      maxConditions: settings.maxConditions,
      maxDescriptionCharacters: settings.maxConditionDescriptionCharacters,
    };
  }

  private trashItem(type: TrashItem['type'], originalId: string, displayName: string, payload: TrashItem['payload']): TrashItem {
    return { trashId: uuid(), type, originalId, displayName, deletedAtUtc: nowIso(), payload: structuredClone(payload) };
  }
}

