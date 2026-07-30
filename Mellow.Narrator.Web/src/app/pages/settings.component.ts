import { CommonModule } from '@angular/common';
import { Component, OnInit } from '@angular/core';
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
import { LlmService } from '../core/llm.service';
import { AppSettings } from '../core/models';

@Component({
  imports: [
    CommonModule, FormsModule, RouterLink, MatButtonModule, MatCardModule, MatExpansionModule,
    MatFormFieldModule, MatInputModule, MatProgressBarModule, MatSelectModule,
  ],
  template: `
    <header class="page-header">
      <div><p class="eyebrow">Connection & generation</p><h1>Settings</h1></div>
      <a mat-button routerLink="/trash">Manage trash</a>
    </header>
    @if (busy) { <mat-progress-bar mode="indeterminate"></mat-progress-bar> }
    @if (settings) {
      <mat-card class="feature-card">
        <mat-card-header><mat-card-title>OpenAI-compatible API</mat-card-title><mat-card-subtitle>Requests go directly from this browser to your provider.</mat-card-subtitle></mat-card-header>
        <mat-card-content>
          <div class="form-grid">
            <mat-form-field appearance="outline" class="wide">
              <mat-label>Base URL</mat-label>
              <input matInput [(ngModel)]="settings.baseUrl" placeholder="https://api.openai.com/v1">
            </mat-form-field>
            <mat-form-field appearance="outline">
              <mat-label>Model ID</mat-label>
              <input matInput [(ngModel)]="settings.modelId" list="models">
              <datalist id="models">@for (model of models; track model) { <option [value]="model"></option> }</datalist>
            </mat-form-field>
            <mat-form-field appearance="outline">
              <mat-label>API key</mat-label>
              <input matInput type="password" [(ngModel)]="settings.apiKey" autocomplete="off">
              <mat-hint>Stored only in this browser’s IndexedDB.</mat-hint>
            </mat-form-field>
          </div>
          <div class="actions">
            <button mat-flat-button (click)="save()">Save settings</button>
            <button mat-stroked-button (click)="loadModels()">Load models</button>
            <button mat-stroked-button (click)="test()">Test connection</button>
          </div>
          <p class="notice">Browser security still applies: the provider must allow CORS requests from this page. For a local model server, explicitly allow this page’s origin.</p>
        </mat-card-content>
      </mat-card>

      <mat-accordion multi>
        <mat-expansion-panel expanded>
          <mat-expansion-panel-header><mat-panel-title>Generation</mat-panel-title><mat-panel-description>Context and model parameters</mat-panel-description></mat-expansion-panel-header>
          <div class="form-grid compact">
            <mat-form-field appearance="outline"><mat-label>Timeout · seconds</mat-label><input matInput type="number" [(ngModel)]="settings.requestTimeoutSeconds"></mat-form-field>
            <mat-form-field appearance="outline"><mat-label>Maximum output tokens</mat-label><input matInput type="number" [(ngModel)]="settings.maxOutputTokens"></mat-form-field>
            <mat-form-field appearance="outline"><mat-label>Temperature</mat-label><input matInput type="number" step=".1" [(ngModel)]="settings.temperature"></mat-form-field>
            <mat-form-field appearance="outline"><mat-label>Top P</mat-label><input matInput type="number" step=".1" [(ngModel)]="settings.topP"></mat-form-field>
            <mat-form-field appearance="outline"><mat-label>Reasoning effort</mat-label><input matInput [(ngModel)]="settings.reasoningEffort" placeholder="low, medium, high"></mat-form-field>
            <mat-form-field appearance="outline"><mat-label>Recent turns in context</mat-label><input matInput type="number" [(ngModel)]="settings.recentTurnCount"></mat-form-field>
          </div>
        </mat-expansion-panel>
        <mat-expansion-panel>
          <mat-expansion-panel-header><mat-panel-title>Story structure</mat-panel-title><mat-panel-description>Memory and response shape</mat-panel-description></mat-expansion-panel-header>
          <div class="form-grid compact">
            <mat-form-field appearance="outline"><mat-label>Maximum Bible entries</mat-label><input matInput type="number" [(ngModel)]="settings.maxStoryBibleEntries"></mat-form-field>
            <mat-form-field appearance="outline"><mat-label>Minimum suggestions</mat-label><input matInput type="number" [(ngModel)]="settings.minSuggestedActions"></mat-form-field>
            <mat-form-field appearance="outline"><mat-label>Maximum suggestions</mat-label><input matInput type="number" [(ngModel)]="settings.maxSuggestedActions"></mat-form-field>
            <mat-form-field appearance="outline"><mat-label>Minimum paragraphs</mat-label><input matInput type="number" [(ngModel)]="settings.minParagraphs"></mat-form-field>
            <mat-form-field appearance="outline"><mat-label>Maximum paragraphs</mat-label><input matInput type="number" [(ngModel)]="settings.maxParagraphs"></mat-form-field>
          </div>
        </mat-expansion-panel>
      </mat-accordion>
      <div class="actions end"><button mat-button (click)="reset()">Reset defaults</button></div>
    }
  `,
})
export class SettingsComponent implements OnInit {
  settings?: AppSettings;
  models: string[] = [];
  busy = false;

  constructor(private readonly db: DbService, private readonly llm: LlmService, private readonly snack: MatSnackBar) {}
  async ngOnInit(): Promise<void> { this.settings = await this.db.settings(); }

  async save(): Promise<void> {
    if (!this.settings) return;
    await this.db.saveSettings(this.settings);
    this.snack.open('Settings saved to this browser.', 'Dismiss', { duration: 2500 });
  }

  async loadModels(): Promise<void> {
    if (!this.settings) return;
    await this.run(async () => {
      this.models = await this.llm.loadModels(this.settings!);
      this.snack.open(`Loaded ${this.models.length} models.`, 'Dismiss', { duration: 2500 });
    });
  }

  async test(): Promise<void> {
    if (!this.settings) return;
    await this.run(async () => {
      await this.save();
      this.snack.open(await this.llm.test(this.settings!), 'Dismiss', { duration: 4000 });
    });
  }

  reset(): void { this.settings = defaultSettings(); }

  private async run(action: () => Promise<void>): Promise<void> {
    this.busy = true;
    try { await action(); }
    catch (error) { this.snack.open(error instanceof Error ? error.message : 'Something went wrong.', 'Dismiss', { duration: 7000 }); }
    finally { this.busy = false; }
  }
}
