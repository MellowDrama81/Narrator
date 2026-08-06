import { CommonModule } from '@angular/common';
import { ChangeDetectorRef, Component, OnInit } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatChipsModule } from '@angular/material/chips';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatSnackBar } from '@angular/material/snack-bar';
import { Router, RouterLink } from '@angular/router';
import { DbService } from '../core/db.service';
import { downloadJson, safeFilename } from '../core/download';
import { nowIso, StoryState, uuid } from '../core/models';
import { NarratorService } from '../core/narrator.service';

@Component({
  imports: [CommonModule, FormsModule, RouterLink, MatButtonModule, MatCardModule, MatChipsModule, MatFormFieldModule, MatInputModule],
  template: `
    <section class="hero compact-hero">
      <div><p class="eyebrow">Living narratives</p><h1>Your Stories</h1><p>Every playthrough keeps its own history and evolving Story Bible.</p></div>
      <div class="hero-actions">
        <a mat-flat-button routerLink="/definitions">Start from a definition</a>
        <button mat-stroked-button (click)="importInput.click()">Import JSON</button>
        <input #importInput hidden type="file" accept=".json,application/json" (change)="importFile($event)">
      </div>
    </section>
    @if (!stories.length) {
      <div class="empty-state"><span class="empty-mark">S</span><h2>No stories in progress.</h2><p>Choose a Story Definition and generate its opening scene.</p><a mat-flat-button routerLink="/definitions">Browse definitions</a></div>
    } @else {
      <div class="card-grid">
        @for (story of stories; track story.id; let index = $index) {
          <mat-card class="story-card">
            <mat-card-header>
              <div mat-card-avatar class="monogram amber">{{ story.label.slice(0,1).toUpperCase() }}</div>
              <mat-card-title>{{ story.label }}</mat-card-title>
              <mat-card-subtitle>{{ story.turns.length }} turns · {{ story.lastActionAtUtc ? ('Played ' + (story.lastActionAtUtc | date:'mediumDate')) : 'Opening scene' }}</mat-card-subtitle>
            </mat-card-header>
            <mat-card-content>
              <p>{{ lastNarration(story) }}</p>
              <mat-chip-set><mat-chip>{{ story.currentStoryBible.length }} Bible entries</mat-chip><mat-chip>{{ story.currentPlannedEvents.length }} Planned Events</mat-chip><mat-chip>Started {{ story.startedAtUtc | date:'mediumDate' }}</mat-chip></mat-chip-set>
              @if (renaming === story.id) {
                <mat-form-field appearance="outline" class="rename"><mat-label>Story label</mat-label><input matInput [(ngModel)]="story.label" (keyup.enter)="saveLabel(story)"></mat-form-field>
              }
            </mat-card-content>
            <mat-card-actions align="end">
              <button mat-button [disabled]="index === 0" (click)="move(index, -1)">Earlier</button>
              <button mat-button [disabled]="index === stories.length - 1" (click)="move(index, 1)">Later</button>
              <button mat-button (click)="renaming = story.id">Label</button>
              <button mat-button (click)="copy(story)">Copy</button>
              <button mat-button (click)="export(story)">Export</button>
              <a mat-flat-button [routerLink]="['/stories', story.id]">Continue</a>
            </mat-card-actions>
          </mat-card>
        }
      </div>
    }
  `,
})
export class StoriesComponent implements OnInit {
  stories: StoryState[] = [];
  renaming = '';

  constructor(
    private readonly db: DbService,
    private readonly narrator: NarratorService,
    private readonly router: Router,
    private readonly snack: MatSnackBar,
    private readonly changeDetector: ChangeDetectorRef,
  ) {}

  async ngOnInit(): Promise<void> { await this.reload(); }
  lastNarration(story: StoryState): string { return story.turns.at(-1)?.narration ?? 'No narration yet.'; }

  async saveLabel(story: StoryState): Promise<void> {
    story.label = story.label.trim() || story.definition.title;
    await this.db.saveStory(story);
    this.renaming = '';
  }

  async copy(story: StoryState): Promise<void> {
    const copy = await this.narrator.copyStory(story);
    await this.router.navigate(['/stories', copy.id]);
  }

  export(story: StoryState): void {
    downloadJson(`${safeFilename(story.label)}-story.json`, { formatVersion: 1, exportedAtUtc: nowIso(), story });
  }

  async move(index: number, delta: number): Promise<void> {
    const other = this.stories[index + delta];
    if (!other) return;
    const first = this.stories[index];
    [first.sortOrder, other.sortOrder] = [other.sortOrder, first.sortOrder];
    await Promise.all([this.db.saveStory(first), this.db.saveStory(other)]);
    await this.reload();
  }

  async importFile(event: Event): Promise<void> {
    const input = event.target as HTMLInputElement;
    const file = input.files?.[0];
    input.value = '';
    if (!file) return;
    try {
      const raw = JSON.parse(await file.text());
      let source = raw.story ?? raw;
      let turns = source.turns ?? raw.turns ?? [];
      if (raw.state) {
        const state = raw.state;
        source = {
          ...state,
          definition: state.setup?.definition ?? state.definition,
          currentStoryBible: state.currentStoryBible?.entries ?? state.currentStoryBible ?? [],
          currentPlannedEvents: state.currentPlannedEvents?.entries ?? state.currentPlannedEvents ?? [],
          turns,
        };
      }
      const stories = await this.db.stories();
      const id = await this.db.story(source.id) ? uuid() : String(source.id ?? uuid());
      const definition = source.definition ?? {};
      const imported: StoryState = {
        id,
        label: String(source.label ?? source.definition?.title ?? 'Imported story'),
        sourceStoryDefinitionId: source.sourceStoryDefinitionId ?? null,
        definition: {
          ...definition,
          initialPlannedEvents: definition.initialPlannedEvents?.entries ?? definition.initialPlannedEvents ?? [],
          initialVictoryConditions: definition.initialVictoryConditions?.entries ?? definition.initialVictoryConditions ?? [],
          initialLossConditions: definition.initialLossConditions?.entries ?? definition.initialLossConditions ?? [],
        },
        currentStoryBible: source.currentStoryBible?.entries ?? source.currentStoryBible ?? [],
        currentPlannedEvents: source.currentPlannedEvents?.entries ?? source.currentPlannedEvents ?? [],
        currentVictoryConditions: source.currentVictoryConditions?.entries ?? source.currentVictoryConditions ?? [],
        currentLossConditions: source.currentLossConditions?.entries ?? source.currentLossConditions ?? [],
        revealedVictoryConditionIds: source.revealedVictoryConditionIds ?? [],
        metVictoryConditionIds: source.metVictoryConditionIds ?? [],
        revealedLossConditionIds: source.revealedLossConditionIds ?? [],
        metLossConditionIds: source.metLossConditionIds ?? [],
        sortOrder: stories.length ? Math.max(...stories.map(x => x.sortOrder)) + 1 : 0,
        startedAtUtc: source.startedAtUtc ?? nowIso(),
        lastActionAtUtc: source.lastActionAtUtc ?? null,
        turns: turns.map((turn: any, index: number) => ({
          ...turn,
          id: turn.id ?? uuid(),
          storyStateId: id,
          sequenceNumber: turn.sequenceNumber ?? index,
          modelId: turn.modelId ?? turn.generation?.modelId ?? 'imported',
          relevantPlannedEventIds: turn.relevantPlannedEventIds ?? [],
          plannedEventUpdates: turn.plannedEventUpdates ?? [],
          revealedVictoryConditionIds: turn.revealedVictoryConditionIds ?? [],
          metVictoryConditionIds: turn.metVictoryConditionIds ?? [],
          revealedLossConditionIds: turn.revealedLossConditionIds ?? [],
          metLossConditionIds: turn.metLossConditionIds ?? [],
        })),
      };
      await this.db.saveStory(imported);
      await this.reload();
      this.snack.open('Story imported.', 'Dismiss', { duration: 2500 });
    } catch (error) {
      this.snack.open(error instanceof Error ? error.message : 'Could not import that story.', 'Dismiss', { duration: 7000 });
    }
  }

  private async reload(): Promise<void> {
    this.stories = (await this.db.stories()).sort((a, b) => a.sortOrder - b.sortOrder || a.label.localeCompare(b.label));
    this.changeDetector.markForCheck();
  }
}
