import { CommonModule } from '@angular/common';
import { ChangeDetectorRef, Component, OnInit } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatExpansionModule } from '@angular/material/expansion';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatSelectModule } from '@angular/material/select';
import { MatSnackBar } from '@angular/material/snack-bar';
import { RouterLink } from '@angular/router';
import { DbService } from '../core/db.service';
import { defaultSettings } from '../core/defaults';
import { AppSettings } from '../core/models';
import { validateSettings } from '../core/settings-validator';

@Component({
  imports: [
    CommonModule, FormsModule, RouterLink, MatButtonModule, MatCardModule, MatExpansionModule,
    MatFormFieldModule, MatInputModule, MatProgressBarModule, MatSelectModule,
  ],
  template: `
    <header class="page-header">
      <div><p class="eyebrow">Generation</p><h1>Settings</h1></div>
      <div><a mat-button routerLink="/connections">API connections</a><a mat-button routerLink="/pipeline">Pipeline calls</a><a mat-button routerLink="/trash">Manage trash</a></div>
    </header>
    @if (busy) { <mat-progress-bar mode="indeterminate"></mat-progress-bar> }
    @if (storageError) {
      <p class="notice storage-warning" role="alert">{{ storageError }}</p>
    }

    <mat-accordion multi>
      <mat-expansion-panel expanded>
        <mat-expansion-panel-header>
          <mat-panel-title>Generation</mat-panel-title>
          <mat-panel-description>Context and model parameters</mat-panel-description>
        </mat-expansion-panel-header>
        <div class="form-grid compact">
          <mat-form-field appearance="outline"><mat-label>Recent turns in context</mat-label><input matInput type="number" [(ngModel)]="settings.recentTurnCount"></mat-form-field>
        </div>
        <mat-form-field appearance="outline">
          <mat-label>Turn generation pipeline</mat-label>
          <mat-select [(ngModel)]="settings.turnPipeline">
            <mat-option value="oneCall">1 call (standard)</mat-option>
            <mat-option value="twoCalls">2 calls (draft + state)</mat-option>
            <mat-option value="threeCalls">3 calls (adjudicate + draft + state)</mat-option>
            <mat-option value="fourCalls">4 calls (experimental)</mat-option>
            <mat-option value="fiveCalls">5 calls (adds plan critic)</mat-option>
            <mat-option value="sevenCalls">7 calls (full sequential analysis)</mat-option>
            <mat-option value="sevenCallsParallel">7 calls (parallel analysis)</mat-option>
            <mat-option value="eightCalls">8 calls (full analysis + prose revision)</mat-option>
          </mat-select>
        </mat-form-field>
        <p class="notice">2 calls separate draft and state. 3â€“5 calls add adjudication, planning, and a plan critic. 7 calls add Story Bible, event, and condition/summary analysis; its parallel variant is faster. 8 calls also revises the prose. More calls cost more.</p>
      </mat-expansion-panel>
      <mat-expansion-panel>
        <mat-expansion-panel-header>
          <mat-panel-title>Narration shape</mat-panel-title>
          <mat-panel-description>Suggestions, paragraphs, and sentences per response</mat-panel-description>
        </mat-expansion-panel-header>
        <div class="form-grid compact">
          <mat-form-field appearance="outline"><mat-label>Minimum suggestions</mat-label><input matInput type="number" [(ngModel)]="settings.minSuggestedActions"></mat-form-field>
          <mat-form-field appearance="outline"><mat-label>Maximum suggestions</mat-label><input matInput type="number" [(ngModel)]="settings.maxSuggestedActions"></mat-form-field>
          <mat-form-field appearance="outline"><mat-label>Maximum suggestion characters</mat-label><input matInput type="number" [(ngModel)]="settings.maxSuggestedActionCharacters"></mat-form-field>
          <mat-form-field appearance="outline"><mat-label>Minimum paragraphs</mat-label><input matInput type="number" [(ngModel)]="settings.minParagraphs"></mat-form-field>
          <mat-form-field appearance="outline"><mat-label>Maximum paragraphs</mat-label><input matInput type="number" [(ngModel)]="settings.maxParagraphs"></mat-form-field>
          <mat-form-field appearance="outline"><mat-label>Minimum sentences per paragraph</mat-label><input matInput type="number" [(ngModel)]="settings.minSentencesPerParagraph"></mat-form-field>
          <mat-form-field appearance="outline"><mat-label>Maximum sentences per paragraph</mat-label><input matInput type="number" [(ngModel)]="settings.maxSentencesPerParagraph"></mat-form-field>
          <mat-form-field appearance="outline"><mat-label>Maximum narration characters</mat-label><input matInput type="number" [(ngModel)]="settings.maxNarrationCharacters"></mat-form-field>
          <mat-form-field appearance="outline"><mat-label>Maximum player action characters</mat-label><input matInput type="number" [(ngModel)]="settings.maxPlayerActionCharacters"></mat-form-field>
        </div>
      </mat-expansion-panel>
      <mat-expansion-panel>
        <mat-expansion-panel-header>
          <mat-panel-title>Story Bible</mat-panel-title>
          <mat-panel-description>Persistent facts remembered across turns</mat-panel-description>
        </mat-expansion-panel-header>
        <div class="form-grid compact">
          <mat-form-field appearance="outline"><mat-label>Maximum Bible entries</mat-label><input matInput type="number" [(ngModel)]="settings.maxStoryBibleEntries"></mat-form-field>
          <mat-form-field appearance="outline"><mat-label>Maximum characters per entry</mat-label><input matInput type="number" [(ngModel)]="settings.maxStoryBibleEntryCharacters"></mat-form-field>
          <mat-form-field appearance="outline"><mat-label>Maximum total characters</mat-label><input matInput type="number" [(ngModel)]="settings.maxStoryBibleCharacters"></mat-form-field>
          <mat-form-field appearance="outline"><mat-label>Capacity warning percent</mat-label><input matInput type="number" [(ngModel)]="settings.storyBibleWarningPercent"></mat-form-field>
          <mat-form-field appearance="outline"><mat-label>Maximum category characters</mat-label><input matInput type="number" [(ngModel)]="settings.maxStoryBibleCategoryCharacters"></mat-form-field>
          <mat-form-field appearance="outline"><mat-label>Maximum name characters</mat-label><input matInput type="number" [(ngModel)]="settings.maxStoryBibleNameCharacters"></mat-form-field>
          <mat-form-field appearance="outline"><mat-label>Maximum updates per response</mat-label><input matInput type="number" [(ngModel)]="settings.maxStoryBibleUpdatesPerResponse"></mat-form-field>
        </div>
      </mat-expansion-panel>
      <mat-expansion-panel>
        <mat-expansion-panel-header>
          <mat-panel-title>Planned Events</mat-panel-title>
          <mat-panel-description>Secret plans and capacity</mat-panel-description>
        </mat-expansion-panel-header>
        <div class="form-grid compact">
          <mat-form-field appearance="outline"><mat-label>Maximum Planned Events</mat-label><input matInput type="number" [(ngModel)]="settings.maxPlannedEvents"></mat-form-field>
          <mat-form-field appearance="outline"><mat-label>Capacity warning percent</mat-label><input matInput type="number" [(ngModel)]="settings.plannedEventsWarningPercent"></mat-form-field>
          <mat-form-field appearance="outline"><mat-label>Maximum characters per event</mat-label><input matInput type="number" [(ngModel)]="settings.maxPlannedEventCharacters"></mat-form-field>
          <mat-form-field appearance="outline"><mat-label>Maximum total characters</mat-label><input matInput type="number" [(ngModel)]="settings.maxPlannedEventsCharacters"></mat-form-field>
          <mat-form-field appearance="outline"><mat-label>Maximum description characters</mat-label><input matInput type="number" [(ngModel)]="settings.maxPlannedEventDescriptionCharacters"></mat-form-field>
          <mat-form-field appearance="outline"><mat-label>Maximum condition characters</mat-label><input matInput type="number" [(ngModel)]="settings.maxPlannedEventConditionCharacters"></mat-form-field>
          <mat-form-field appearance="outline"><mat-label>Maximum updates per response</mat-label><input matInput type="number" [(ngModel)]="settings.maxPlannedEventUpdatesPerResponse"></mat-form-field>
        </div>
      </mat-expansion-panel>
      <mat-expansion-panel>
        <mat-expansion-panel-header>
          <mat-panel-title>Content limits</mat-panel-title>
          <mat-panel-description>Story metadata and victory/loss conditions</mat-panel-description>
        </mat-expansion-panel-header>
        <div class="form-grid compact">
          <mat-form-field appearance="outline"><mat-label>Maximum title characters</mat-label><input matInput type="number" [(ngModel)]="settings.maxStoryTitleCharacters"></mat-form-field>
          <mat-form-field appearance="outline"><mat-label>Maximum label characters</mat-label><input matInput type="number" [(ngModel)]="settings.maxStoryLabelCharacters"></mat-form-field>
          <mat-form-field appearance="outline"><mat-label>Maximum Story Definition Prompt / Story Prompt characters</mat-label><input matInput type="number" [(ngModel)]="settings.maxStoryPromptCharacters"></mat-form-field>
          <mat-form-field appearance="outline"><mat-label>Maximum conditions</mat-label><input matInput type="number" [(ngModel)]="settings.maxConditions"></mat-form-field>
          <mat-form-field appearance="outline"><mat-label>Maximum condition characters</mat-label><input matInput type="number" [(ngModel)]="settings.maxConditionDescriptionCharacters"></mat-form-field>
          <mat-form-field appearance="outline"><mat-label>Story summary characters</mat-label><input matInput type="number" [(ngModel)]="settings.maxStorySummaryCharacters"></mat-form-field>
        </div>
      </mat-expansion-panel>
    </mat-accordion>
    <div class="actions end"><button mat-flat-button (click)="save()">Save settings</button><button mat-button (click)="reset()">Reset defaults</button></div>
  `,
})
export class SettingsComponent implements OnInit {
  settings: AppSettings = defaultSettings();
  busy = false;
  storageError = '';

  // The baseUrl/modelId last known to be persisted, so a save can tell whether the connection is
  // changing and reset the negotiated capability state accordingly (mirrors
  // NarratorApplication.SaveSettingsAsync's reset-on-change logic in Settings.cs).
  private lastSavedBaseUrl = '';
  private lastSavedModelId = '';

  constructor(
    private readonly db: DbService,
    private readonly snack: MatSnackBar,
    private readonly changeDetector: ChangeDetectorRef,
  ) {}

  async ngOnInit(): Promise<void> {
    try {
      this.settings = await this.db.settings();
      this.lastSavedBaseUrl = this.settings.baseUrl;
      this.lastSavedModelId = this.settings.modelId;
    } catch {
      this.storageError = 'Browser storage could not be opened. You can configure this session, but settings may not persist until IndexedDB access is available.';
    } finally {
      this.changeDetector.markForCheck();
    }
  }

  async save(): Promise<void> {
    await this.run(async () => {
      await this.persistSettings();
      this.snack.open('Settings saved to this browser.', 'Dismiss', { duration: 2500 });
    });
  }

  reset(): void {
    this.settings = defaultSettings();
  }

  // Validates, resets negotiated connection capabilities if the endpoint or model changed, and saves.
  // Throws (surfaced by run() as a snackbar) rather than persisting when validation fails - this is
  // what stops a blank field's NaN, or minSuggestedActions > maxSuggestedActions, from being saved.
  private async persistSettings(): Promise<void> {
    if (this.settings.baseUrl !== this.lastSavedBaseUrl || this.settings.modelId !== this.lastSavedModelId) {
      this.settings.structuredOutputTier = 'untested';
      this.settings.outputTokenParameter = 'maxCompletionTokens';
      this.settings.instructionMessageRole = 'developer';
    }

    const errors = Object.entries(validateSettings(this.settings));
    if (errors.length > 0) {
      throw new Error(errors.slice(0, 5).map(([field, message]) => `${field}: ${message}`).join('\n'));
    }

    await this.db.saveSettings(this.settings);
    this.lastSavedBaseUrl = this.settings.baseUrl;
    this.lastSavedModelId = this.settings.modelId;
    this.storageError = '';
  }

  private async run(action: () => Promise<void>): Promise<void> {
    this.busy = true;
    try {
      await action();
    } catch (error) {
      this.snack.open(error instanceof Error ? error.message : 'Something went wrong.', 'Dismiss', { duration: 7000 });
    } finally {
      this.busy = false;
      this.changeDetector.markForCheck();
    }
  }
}
