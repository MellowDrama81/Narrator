import { TestBed } from '@angular/core/testing';
import { MatSnackBar } from '@angular/material/snack-bar';
import { provideRouter } from '@angular/router';
import { vi } from 'vitest';
import { DbService } from '../core/db.service';
import { defaultSettings } from '../core/defaults';
import { StoryState } from '../core/models';
import { NarratorService } from '../core/narrator.service';
import { StoriesComponent } from './stories.component';

describe('StoriesComponent', () => {
  it('renders stories loaded asynchronously from IndexedDB', async () => {
    const story = {
      id: 'story-id',
      label: 'The Glass City',
      sourceStoryDefinitionId: 'definition-id',
      definition: {
        title: 'The Glass City',
        storyPrompt: 'A city made of memory.',
        initialEventsPrompt: '',
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
      startedAtUtc: new Date().toISOString(),
      lastActionAtUtc: null,
      turns: [{
        id: 'turn-id',
        storyStateId: 'story-id',
        sequenceNumber: 0,
        playerAction: null,
        narration: 'The glass streets wake beneath your feet.',
        suggestedActions: [],
        relevantStoryBibleEntryIds: [],
        storyBibleUpdates: [],
        relevantPlannedEventIds: [],
        plannedEventUpdates: [],
        revealedVictoryConditionIds: [],
        metVictoryConditionIds: [],
        revealedLossConditionIds: [],
        metLossConditionIds: [],
        completedAtUtc: new Date().toISOString(),
        modelId: 'test-model',
      }],
    } satisfies StoryState;

    await TestBed.configureTestingModule({
      imports: [StoriesComponent],
      providers: [
        provideRouter([]),
        { provide: DbService, useValue: { stories: vi.fn(async () => [story]) } },
        { provide: NarratorService, useValue: {} },
        { provide: MatSnackBar, useValue: { open: vi.fn() } },
      ],
    }).compileComponents();

    const fixture = TestBed.createComponent(StoriesComponent);
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();

    expect(fixture.componentInstance.stories).toHaveLength(1);
    expect((fixture.nativeElement as HTMLElement).textContent).toContain('The Glass City');
  });

  it('imports a well-formed story file', async () => {
    const persisted: StoryState[] = [];
    const database = {
      stories: vi.fn(async () => [...persisted]),
      story: vi.fn(async () => undefined),
      settings: vi.fn(async () => defaultSettings()),
      saveStory: vi.fn(async (story: StoryState) => { persisted.push(story); }),
    };

    await TestBed.configureTestingModule({
      imports: [StoriesComponent],
      providers: [
        provideRouter([]),
        { provide: DbService, useValue: database },
        { provide: NarratorService, useValue: {} },
        { provide: MatSnackBar, useValue: { open: vi.fn() } },
      ],
    }).compileComponents();

    const fixture = TestBed.createComponent(StoriesComponent);
    fixture.detectChanges();
    await fixture.whenStable();

    const file = {
      text: async () => JSON.stringify({
        formatVersion: 1,
        story: {
          id: 'story-1',
          label: 'The Lighthouse Story',
          definition: {
            title: 'The Lighthouse',
            storyPrompt: 'A keeper guards a light that must never go out.',
            initialStoryBible: [],
          },
          turns: [
            {
              sequenceNumber: 0,
              playerAction: null,
              narration: 'The lamp is lit for the night watch.',
              modelId: 'test-model',
              completedAtUtc: '2026-01-01T00:00:00.000Z',
            },
          ],
        },
      }),
    } as File;
    const input = document.createElement('input');
    Object.defineProperty(input, 'files', { value: [file] });

    await fixture.componentInstance.importFile({ target: input } as unknown as Event);

    expect(database.saveStory).toHaveBeenCalledOnce();
    expect(fixture.componentInstance.stories).toHaveLength(1);
  });

  it('rejects a story import whose turn sequence numbers are not contiguous, instead of saving it', async () => {
    const persisted: StoryState[] = [];
    const database = {
      stories: vi.fn(async () => [...persisted]),
      story: vi.fn(async () => undefined),
      settings: vi.fn(async () => defaultSettings()),
      saveStory: vi.fn(async (story: StoryState) => { persisted.push(story); }),
    };
    const snack = { open: vi.fn() };

    await TestBed.configureTestingModule({
      imports: [StoriesComponent],
      providers: [
        provideRouter([]),
        { provide: DbService, useValue: database },
        { provide: NarratorService, useValue: {} },
        { provide: MatSnackBar, useValue: snack },
      ],
    }).compileComponents();

    const fixture = TestBed.createComponent(StoriesComponent);
    fixture.detectChanges();
    await fixture.whenStable();

    const file = {
      text: async () => JSON.stringify({
        formatVersion: 1,
        story: {
          id: 'story-1',
          label: 'The Lighthouse Story',
          definition: {
            title: 'The Lighthouse',
            storyPrompt: 'A keeper guards a light that must never go out.',
            initialStoryBible: [],
          },
          turns: [
            {
              sequenceNumber: 0, playerAction: null, narration: 'The lamp is lit.',
              modelId: 'test-model', completedAtUtc: '2026-01-01T00:00:00.000Z',
            },
            {
              sequenceNumber: 2, playerAction: 'Check the oil.', narration: 'The oil is checked.',
              modelId: 'test-model', completedAtUtc: '2026-01-01T00:05:00.000Z',
            },
          ],
        },
      }),
    } as File;
    const input = document.createElement('input');
    Object.defineProperty(input, 'files', { value: [file] });

    await fixture.componentInstance.importFile({ target: input } as unknown as Event);

    expect(database.saveStory).not.toHaveBeenCalled();
    expect(fixture.componentInstance.stories).toHaveLength(0);
    expect(snack.open).toHaveBeenCalledWith(expect.stringMatching(/not contiguous/), 'Dismiss', expect.anything());
  });
});
