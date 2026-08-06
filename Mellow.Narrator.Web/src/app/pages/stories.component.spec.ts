import { TestBed } from '@angular/core/testing';
import { MatSnackBar } from '@angular/material/snack-bar';
import { provideRouter } from '@angular/router';
import { vi } from 'vitest';
import { DbService } from '../core/db.service';
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
});
