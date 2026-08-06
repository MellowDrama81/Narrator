import { CommonModule } from '@angular/common';
import { ChangeDetectorRef, Component, OnInit } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatSnackBar } from '@angular/material/snack-bar';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { DbService } from '../core/db.service';
import { downloadJson, safeFilename } from '../core/download';
import { nowIso, PlannedEvent, StoryBibleEntry, StoryCondition, StoryDefinition } from '../core/models';
import { NarratorService } from '../core/narrator.service';
import { validateBibleEntry } from '../core/story-bible';
import { BibleEditorComponent } from '../shared/bible-editor.component';
import { ConditionsEditorComponent } from '../shared/conditions-editor.component';
import { PlannedEventsEditorComponent } from '../shared/planned-events-editor.component';

@Component({
  imports: [
    CommonModule, FormsModule, RouterLink, MatButtonModule, MatCardModule,
    MatFormFieldModule, MatInputModule, MatProgressBarModule, BibleEditorComponent, PlannedEventsEditorComponent,
    ConditionsEditorComponent,
  ],
  template: `
    <header class="page-header">
      <div>
        <a class="back-link" routerLink="/definitions">← Definitions</a>
        <p class="eyebrow">{{ creating ? 'New world' : 'Definition editor' }}</p>
        <h1>{{ creating ? 'Begin with a premise' : definition?.title }}</h1>
      </div>
      @if (definition) {
        <div class="actions">
          <button mat-stroked-button (click)="export()">Export</button>
          <button mat-flat-button [disabled]="busy" (click)="start()">Start story</button>
        </div>
      }
    </header>
    @if (busy) { <mat-progress-bar mode="indeterminate"></mat-progress-bar> }

    @if (creating) {
      <mat-card class="prompt-card">
        <mat-card-content>
          <p class="lead">Describe the immutable idea. Characters, secrets, relationships, and changing facts will be organized into the Story Bible.</p>
          <mat-form-field appearance="outline">
            <mat-label>Optional title</mat-label>
            <input matInput [(ngModel)]="draftTitle" (ngModelChange)="saveDraft()">
          </mat-form-field>
          <mat-form-field appearance="outline">
            <mat-label>Story premise</mat-label>
            <textarea matInput rows="12" [(ngModel)]="draftPrompt" (ngModelChange)="saveDraft()" placeholder="You awaken aboard a silent orbital station where every clock has stopped..."></textarea>
            <mat-hint>Include tone, setting, the player character, and the central tension.</mat-hint>
          </mat-form-field>
        </mat-card-content>
        <mat-card-actions align="end"><button mat-flat-button [disabled]="busy || !draftPrompt.trim()" (click)="generate()">Generate Story Definition</button></mat-card-actions>
      </mat-card>
    } @else if (definition) {
      <mat-card class="editor-card">
        <mat-card-content>
          <div class="editor-fields">
            <mat-form-field appearance="outline">
              <mat-label>Title</mat-label>
              <input matInput [(ngModel)]="definition.title">
            </mat-form-field>
            <mat-form-field appearance="outline">
              <mat-label>Story Prompt · immutable world and narration rules</mat-label>
              <textarea matInput rows="9" [(ngModel)]="definition.storyPrompt"></textarea>
            </mat-form-field>
            <mat-form-field appearance="outline">
              <mat-label>Initial Events · early-scene guidance</mat-label>
              <textarea matInput rows="5" [(ngModel)]="definition.initialEventsPrompt"></textarea>
            </mat-form-field>
          </div>
          <button mat-flat-button (click)="save()">Save definition</button>
          <button mat-button class="danger" (click)="remove()">Move to trash</button>
        </mat-card-content>
      </mat-card>
      <div class="cull-row">
        <button mat-stroked-button [disabled]="busy" (click)="cullToLimits()">Cull to limits</button>
      </div>
      <app-bible-editor [(entries)]="definition.initialStoryBible"></app-bible-editor>
      <app-planned-events-editor [(entries)]="definition.initialPlannedEvents"></app-planned-events-editor>
      <app-conditions-editor [(entries)]="definition.initialVictoryConditions" heading="Victory Conditions"></app-conditions-editor>
      <app-conditions-editor [(entries)]="definition.initialLossConditions" heading="Loss Conditions"></app-conditions-editor>
    }
  `,
})
export class DefinitionEditorComponent implements OnInit {
  creating = false;
  definition?: StoryDefinition;
  draftTitle = '';
  draftPrompt = '';
  busy = false;

  constructor(
    private readonly route: ActivatedRoute,
    private readonly router: Router,
    private readonly db: DbService,
    private readonly narrator: NarratorService,
    private readonly snack: MatSnackBar,
    private readonly changeDetector: ChangeDetectorRef,
  ) {}

  async ngOnInit(): Promise<void> {
    const id = this.route.snapshot.paramMap.get('id')!;
    this.creating = id === 'new';
    if (this.creating) {
      const draft = await this.db.meta<{ title: string; prompt: string }>('definition-draft');
      this.draftTitle = draft?.title ?? '';
      this.draftPrompt = draft?.prompt ?? '';
    } else {
      this.definition = await this.db.definition(id);
      if (!this.definition) await this.router.navigate(['/definitions']);
    }
    this.changeDetector.markForCheck();
  }

  saveDraft(): void { void this.db.saveMeta('definition-draft', { title: this.draftTitle, prompt: this.draftPrompt }); }

  async generate(): Promise<void> {
    this.busy = true;
    try {
      const value = await this.narrator.generateDefinition(this.draftTitle, this.draftPrompt);
      await this.db.saveMeta('definition-draft', { title: '', prompt: '' });
      await this.router.navigate(['/definitions', value.id]);
    } catch (error) { this.error(error); }
    finally {
      this.busy = false;
      this.changeDetector.markForCheck();
    }
  }

  async save(): Promise<void> {
    if (!this.definition) return;
    const bible = this.normalizeBible(this.definition.initialStoryBible);
    const bibleError = bible.map(validateBibleEntry).find(Boolean);
    if (bibleError) { this.snack.open(bibleError, 'Dismiss', { duration: 7000 }); return; }
    this.definition.updatedAtUtc = nowIso();
    this.definition.initialStoryBible = bible;
    this.definition.initialPlannedEvents = this.cleanPlannedEvents(this.definition.initialPlannedEvents);
    this.definition.initialVictoryConditions = this.cleanConditions(this.definition.initialVictoryConditions);
    this.definition.initialLossConditions = this.cleanConditions(this.definition.initialLossConditions);
    await this.db.saveDefinition(this.definition);
    this.snack.open('Definition saved.', 'Dismiss', { duration: 2500 });
  }

  async start(): Promise<void> {
    if (!this.definition) return;
    this.busy = true;
    try {
      await this.save();
      const story = await this.narrator.startStory(this.definition);
      await this.router.navigate(['/stories', story.id]);
    } catch (error) { this.error(error); }
    finally {
      this.busy = false;
      this.changeDetector.markForCheck();
    }
  }

  export(): void {
    if (!this.definition) return;
    downloadJson(`${safeFilename(this.definition.title)}-definition.json`, { formatVersion: 1, exportedAtUtc: nowIso(), definition: this.definition });
  }

  // Trims the initial Story Bible/Planned Events down to the currently configured limits - mirrors
  // NarratorApplication.CullDefinitionAsync. Confirms first, then reports exactly what was removed
  // (rather than culling silently), diffing the before/after entry ids since cullDefinition() itself
  // returns only the updated Definition.
  async cullToLimits(): Promise<void> {
    if (!this.definition) return;
    if (!confirm('Cull the Story Bible and Planned Events down to the currently configured limits? Lower-importance or less-recently-relevant entries may be removed.')) return;
    this.busy = true;
    try {
      const before = this.definition;
      const after = await this.narrator.cullDefinition(before);
      this.definition = after;
      this.reportCulled(before.initialStoryBible, after.initialStoryBible, before.initialPlannedEvents, after.initialPlannedEvents);
    } catch (error) { this.error(error); }
    finally {
      this.busy = false;
      this.changeDetector.markForCheck();
    }
  }

  private reportCulled(
    bibleBefore: StoryBibleEntry[], bibleAfter: StoryBibleEntry[],
    eventsBefore: PlannedEvent[], eventsAfter: PlannedEvent[],
  ): void {
    const survivingBibleIds = new Set(bibleAfter.map(x => x.id));
    const removedBible = bibleBefore.filter(x => !survivingBibleIds.has(x.id));
    const survivingEventIds = new Set(eventsAfter.map(x => x.id));
    const removedEvents = eventsBefore.filter(x => !survivingEventIds.has(x.id));
    if (!removedBible.length && !removedEvents.length) {
      this.snack.open('Already within limits — nothing removed.', 'Dismiss', { duration: 4000 });
      return;
    }
    const names = [...removedBible.map(x => x.name), ...removedEvents.map(x => x.description)];
    this.snack.open(`Culled to limits. Removed: ${names.join(', ')}`, 'Dismiss', { duration: 12000 });
  }

  async remove(): Promise<void> {
    if (!this.definition || !confirm(`Move “${this.definition.title}” to Trash? Existing stories will remain playable.`)) return;
    await this.narrator.trashDefinition(this.definition);
    await this.router.navigate(['/definitions']);
  }

  private normalizeBible(entries: StoryBibleEntry[]): StoryBibleEntry[] {
    return entries.map(entry => ({
      ...entry, name: entry.name.trim(), category: entry.category.trim(),
      knownFacts: entry.knownFacts.map(x => x.trim()),
      secretFacts: entry.secretFacts.map(x => x.trim()),
    }));
  }

  private cleanPlannedEvents(entries: PlannedEvent[]): PlannedEvent[] {
    return entries
      .map(entry => ({
        ...entry,
        description: entry.description.trim(),
        condition: entry.condition && entry.condition.trim() ? entry.condition.trim() : null,
      }))
      .filter(entry => entry.description);
  }

  private cleanConditions(entries: StoryCondition[]): StoryCondition[] {
    return entries
      .map(entry => ({ ...entry, description: entry.description.trim() }))
      .filter(entry => entry.description);
  }

  private error(error: unknown): void {
    this.snack.open(error instanceof Error ? error.message : 'Something went wrong.', 'Dismiss', { duration: 7000 });
  }
}
