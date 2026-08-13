import { CommonModule } from '@angular/common';
import { ChangeDetectorRef, Component, OnInit } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { MatSnackBar } from '@angular/material/snack-bar';
import { RouterLink } from '@angular/router';
import { DbService } from '../core/db.service';
import { defaultSettings } from '../core/defaults';
import { AppSettings, GenerationCall } from '../core/models';
import { validateSettings } from '../core/settings-validator';

@Component({
  imports: [CommonModule, FormsModule, RouterLink, MatButtonModule, MatCardModule, MatFormFieldModule, MatInputModule, MatSelectModule],
  template: `
    <header class="page-header"><div><a class="back-link" routerLink="/settings">← Settings</a><p class="eyebrow">Providers</p><h1>API connections</h1></div></header>
    <p class="lead">Named connections and the model used by each generation call. API keys are stored only in this browser.</p>
    @for (connection of settings.connections; track connection.id; let index = $index) {
      <mat-card class="feature-card"><mat-card-content><div class="form-grid compact">
        <mat-form-field appearance="outline"><mat-label>Name</mat-label><input matInput [(ngModel)]="connection.name"></mat-form-field>
        <mat-form-field appearance="outline"><mat-label>Base URL</mat-label><input matInput [(ngModel)]="connection.baseUrl" placeholder="https://api.openai.com/v1"></mat-form-field>
        <mat-form-field appearance="outline"><mat-label>API key</mat-label><input matInput type="password" [(ngModel)]="connection.apiKey" autocomplete="off"></mat-form-field>
      </div><button mat-button color="warn" (click)="remove(index)" [disabled]="settings.connections.length === 1">Remove</button></mat-card-content></mat-card>
    }
    <div class="actions"><button mat-stroked-button (click)="add()">Add connection</button><button mat-flat-button (click)="save()">Save connections</button></div>
    <h2>Per-call routing</h2>
    @for (call of generationCalls; track call) {
      <mat-card class="row-card"><mat-card-content><div class="form-grid compact"><strong>{{ label(call) }}</strong>
        <mat-form-field appearance="outline"><mat-label>Connection</mat-label><mat-select [(ngModel)]="route(call).connectionId">@for (connection of settings.connections; track connection.id) { <mat-option [value]="connection.id">{{ connection.name }}</mat-option> }</mat-select></mat-form-field>
        <mat-form-field appearance="outline"><mat-label>Model</mat-label><input matInput [(ngModel)]="route(call).modelId" placeholder="Model ID"></mat-form-field>
      </div></mat-card-content></mat-card>
    }
    <div class="actions end"><button mat-flat-button (click)="save()">Save routing</button></div>
  `,
})
export class ConnectionsComponent implements OnInit {
  settings: AppSettings = defaultSettings();
  readonly generationCalls: GenerationCall[] = ['storyDefinition', 'turn', 'adjudication', 'scenePlan', 'planCritic', 'narration', 'storyBibleAnalysis', 'plannedEventAnalysis', 'conditionSummaryAnalysis', 'stateExtraction', 'proseRevision'];
  constructor(private readonly db: DbService, private readonly snack: MatSnackBar, private readonly changeDetector: ChangeDetectorRef) {}
  async ngOnInit(): Promise<void> { this.settings = await this.db.settings(); this.changeDetector.markForCheck(); }
  add(): void { this.settings.connections.push({ id: crypto.randomUUID(), name: `Connection ${this.settings.connections.length + 1}`, baseUrl: '', apiKey: '' }); }
  remove(index: number): void { const [removed] = this.settings.connections.splice(index, 1); for (const call of this.generationCalls) if (this.route(call).connectionId === removed.id) this.route(call).connectionId = this.settings.connections[0].id; }
  route(call: GenerationCall): { connectionId: string; modelId: string } { return this.settings.generationCallRoutes[call] ??= { connectionId: this.settings.connections[0]?.id ?? '', modelId: this.settings.modelId }; }
  label(call: GenerationCall): string { return call.replace(/([A-Z])/g, ' $1').replace(/^./, char => char.toUpperCase()); }
  async save(): Promise<void> { const errors = Object.values(validateSettings(this.settings)); if (errors.length) { this.snack.open(errors[0], 'Dismiss', { duration: 5000 }); return; } await this.db.saveSettings(this.settings); this.snack.open('Connections and routing saved.', 'Dismiss', { duration: 2500 }); }
}
