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
import { LlmService } from '../core/llm.service';
import { AppSettings } from '../core/models';
import { validateSettings } from '../core/settings-validator';

@Component({
  imports: [CommonModule, FormsModule, RouterLink, MatButtonModule, MatCardModule, MatFormFieldModule, MatInputModule, MatSelectModule],
  template: `
    <header class="page-header"><div><a class="back-link" routerLink="/settings">← Settings</a><p class="eyebrow">Providers</p><h1>API connections</h1></div></header>
    <p class="lead">Named connections. API keys are stored only in this browser.</p>
    @for (connection of settings.connections; track connection.id; let index = $index) {
      <mat-card class="feature-card"><mat-card-content><div class="form-grid compact">
        <mat-form-field appearance="outline"><mat-label>Name</mat-label><input matInput [(ngModel)]="connection.name"></mat-form-field>
        <mat-form-field appearance="outline"><mat-label>Base URL</mat-label><input matInput [(ngModel)]="connection.baseUrl" placeholder="https://api.openai.com/v1"></mat-form-field>
        <mat-form-field appearance="outline"><mat-label>API key</mat-label><input matInput type="password" [(ngModel)]="connection.apiKey" autocomplete="off"></mat-form-field>
      </div><button mat-stroked-button (click)="test(connection)">Test connection</button><button mat-button color="warn" (click)="remove(index)" [disabled]="settings.connections.length === 1">Remove</button></mat-card-content></mat-card>
    }
    <div class="actions"><button mat-stroked-button (click)="add()">Add connection</button><button mat-flat-button (click)="save()">Save connections</button></div>
  `,
})
export class ConnectionsComponent implements OnInit {
  settings: AppSettings = defaultSettings();
  constructor(private readonly db: DbService, private readonly llm: LlmService, private readonly snack: MatSnackBar, private readonly changeDetector: ChangeDetectorRef) {}
  async ngOnInit(): Promise<void> { this.settings = await this.db.settings(); this.changeDetector.markForCheck(); }
  add(): void { this.settings.connections.push({ id: crypto.randomUUID(), name: `Connection ${this.settings.connections.length + 1}`, baseUrl: '', apiKey: '' }); }
  remove(index: number): void {
    const [removed] = this.settings.connections.splice(index, 1);
    const replacement = this.settings.connections[0]?.id ?? '';
    for (const route of Object.values(this.settings.generationCallRoutes))
      if (route?.connectionId === removed.id) route.connectionId = replacement;
  }
  async test(connection: AppSettings['connections'][number]): Promise<void> {
    const route = Object.values(this.settings.generationCallRoutes).find(candidate => candidate?.connectionId === connection.id);
    const modelId = route?.modelId || this.settings.modelId;
    if (!modelId) { this.snack.open('Assign a model to this connection before testing it.', 'Dismiss', { duration: 5000 }); return; }
    try {
      const message = await this.llm.test({ ...this.settings, baseUrl: connection.baseUrl, apiKey: connection.apiKey, modelId });
      this.snack.open(`${connection.name}: ${message}`, 'Dismiss', { duration: 4000 });
    } catch (error) { this.snack.open(error instanceof Error ? error.message : 'Connection test failed.', 'Dismiss', { duration: 6000 }); }
  }
  async save(): Promise<void> { const errors = Object.values(validateSettings(this.settings)); if (errors.length) { this.snack.open(errors[0], 'Dismiss', { duration: 5000 }); return; } await this.db.saveSettings(this.settings); this.snack.open('Connections saved.', 'Dismiss', { duration: 2500 }); }
}
