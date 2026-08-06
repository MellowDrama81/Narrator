import { CommonModule } from '@angular/common';
import { ChangeDetectorRef, Component, OnInit } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatChipsModule } from '@angular/material/chips';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatSnackBar } from '@angular/material/snack-bar';
import { Router, RouterLink } from '@angular/router';
import { DbService } from '../core/db.service';
import { downloadJson, safeFilename } from '../core/download';
import { nowIso, StoryDefinition, uuid } from '../core/models';
import { NarratorService } from '../core/narrator.service';

@Component({
  imports: [CommonModule, FormsModule, RouterLink, MatButtonModule, MatCardModule, MatChipsModule, MatProgressBarModule],
  template: `
    <section class="hero">
      <div>
        <p class="eyebrow">Reusable worlds</p>
        <h1>Story Definitions</h1>
        <p>Shape a premise into a durable world, then begin as many independent stories as you like.</p>
      </div>
      <div class="hero-actions">
        <a mat-flat-button routerLink="/definitions/new">Create definition</a>
        <button mat-stroked-button (click)="importInput.click()">Import JSON</button>
        <input #importInput hidden type="file" accept=".json,application/json" (change)="importFile($event)">
      </div>
    </section>
    @if (busyId) { <mat-progress-bar mode="indeterminate"></mat-progress-bar> }
    @if (!definitions.length) {
      <div class="empty-state">
        <span class="empty-mark">N</span>
        <h2>Your first world starts with a sentence.</h2>
        <p>Describe a setting, a character, or a problem. Mellow Narrator will turn it into a structured Story Definition.</p>
        <a mat-flat-button routerLink="/definitions/new">Write a premise</a>
      </div>
    } @else {
      <div class="card-grid">
        @for (item of definitions; track item.id; let index = $index) {
          <mat-card class="story-card">
            <mat-card-header>
              <div mat-card-avatar class="monogram">{{ item.title.slice(0, 1).toUpperCase() }}</div>
              <mat-card-title>{{ item.title }}</mat-card-title>
              <mat-card-subtitle>Updated {{ item.updatedAtUtc | date:'mediumDate' }}</mat-card-subtitle>
            </mat-card-header>
            <mat-card-content>
              <p>{{ item.storyPrompt }}</p>
              <mat-chip-set><mat-chip>{{ item.initialStoryBible.length }} Bible entries</mat-chip><mat-chip>{{ item.initialPlannedEvents.length }} Planned Events</mat-chip></mat-chip-set>
            </mat-card-content>
            <mat-card-actions align="end">
              <button mat-button [disabled]="index === 0" (click)="move(index, -1)">Earlier</button>
              <button mat-button [disabled]="index === definitions.length - 1" (click)="move(index, 1)">Later</button>
              <button mat-button (click)="export(item)">Export</button>
              <a mat-button [routerLink]="['/definitions', item.id]">Open</a>
              <button mat-flat-button [disabled]="busyId === item.id" (click)="start(item)">Start story</button>
            </mat-card-actions>
          </mat-card>
        }
      </div>
    }
  `,
})
export class DefinitionsComponent implements OnInit {
  definitions: StoryDefinition[] = [];
  busyId = '';

  constructor(
    private readonly db: DbService,
    private readonly narrator: NarratorService,
    private readonly router: Router,
    private readonly snack: MatSnackBar,
    private readonly changeDetector: ChangeDetectorRef,
  ) {}

  async ngOnInit(): Promise<void> { await this.reload(); }

  async start(definition: StoryDefinition): Promise<void> {
    this.busyId = definition.id;
    try {
      const story = await this.narrator.startStory(definition);
      await this.router.navigate(['/stories', story.id]);
    } catch (error) { this.error(error); }
    finally { this.busyId = ''; }
  }

  export(value: StoryDefinition): void {
    downloadJson(`${safeFilename(value.title)}-definition.json`, { formatVersion: 1, exportedAtUtc: nowIso(), definition: value });
  }

  async move(index: number, delta: number): Promise<void> {
    const other = this.definitions[index + delta];
    if (!other) return;
    const first = this.definitions[index];
    [first.sortOrder, other.sortOrder] = [other.sortOrder, first.sortOrder];
    await Promise.all([this.db.saveDefinition(first), this.db.saveDefinition(other)]);
    await this.reload();
  }

  async importFile(event: Event): Promise<void> {
    const input = event.target as HTMLInputElement;
    const file = input.files?.[0];
    input.value = '';
    if (!file) return;
    try {
      const raw = JSON.parse(await file.text());
      const source = raw.definition ?? raw.data?.definition ?? raw;
      const bible = source.initialStoryBible?.entries ?? source.initialStoryBible ?? [];
      const plannedEvents = source.initialPlannedEvents?.entries ?? source.initialPlannedEvents ?? [];
      const victoryConditions = source.initialVictoryConditions?.entries ?? source.initialVictoryConditions ?? [];
      const lossConditions = source.initialLossConditions?.entries ?? source.initialLossConditions ?? [];
      const definitions = await this.db.definitions();
      const imported: StoryDefinition = {
        id: await this.db.definition(source.id) ? uuid() : String(source.id ?? uuid()),
        title: String(source.title ?? 'Imported definition'),
        storyPrompt: String(source.storyPrompt ?? ''),
        initialEventsPrompt: String(source.initialEventsPrompt ?? ''),
        initialStoryBible: bible,
        initialPlannedEvents: plannedEvents,
        initialVictoryConditions: victoryConditions,
        initialLossConditions: lossConditions,
        sortOrder: definitions.length ? Math.max(...definitions.map(x => x.sortOrder)) + 1 : 0,
        createdAtUtc: source.createdAtUtc ?? nowIso(),
        updatedAtUtc: nowIso(),
      };
      await this.db.saveDefinition(imported);
      await this.reload();
      this.snack.open('Story Definition imported.', 'Dismiss', { duration: 2500 });
    } catch (error) { this.error(error); }
  }

  private async reload(): Promise<void> {
    this.definitions = (await this.db.definitions()).sort((a, b) => a.sortOrder - b.sortOrder || a.title.localeCompare(b.title));
    this.changeDetector.markForCheck();
  }

  private error(error: unknown): void {
    this.snack.open(error instanceof Error ? error.message : 'Something went wrong.', 'Dismiss', { duration: 7000 });
  }
}
