import { CommonModule } from '@angular/common';
import { ChangeDetectorRef, Component, OnInit } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatExpansionModule } from '@angular/material/expansion';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { MatSnackBar } from '@angular/material/snack-bar';
import { RouterLink } from '@angular/router';
import { DbService } from '../core/db.service';
import { defaultSettings } from '../core/defaults';
import { LlmService } from '../core/llm.service';
import { AppSettings, GenerationCall, GenerationCallRoute, TurnPipelineMode } from '../core/models';
import { validateSettings } from '../core/settings-validator';

@Component({
  imports: [CommonModule, FormsModule, RouterLink, MatButtonModule, MatCardModule, MatExpansionModule, MatFormFieldModule, MatInputModule, MatSelectModule],
  template: `
    <header class="page-header"><div><a class="back-link" routerLink="/settings">← Settings</a><p class="eyebrow">Generation</p><h1>Pipeline calls</h1></div></header>
    <p class="lead">Set the connection and model for each active call. Request behavior is available under Advanced for calls that need an override.</p>
    @for (call of selectedCalls; track call) {
      <mat-card class="feature-card"><mat-card-content><h2>{{ label(call) }}</h2><div class="form-grid compact">
        <mat-form-field appearance="outline"><mat-label>Connection</mat-label><mat-select [(ngModel)]="route(call).connectionId">@for (connection of settings.connections; track connection.id) { <mat-option [value]="connection.id">{{ connection.name }}</mat-option> }</mat-select></mat-form-field>
        <mat-form-field appearance="outline"><mat-label>Model</mat-label><input matInput [(ngModel)]="route(call).modelId" placeholder="Model ID"></mat-form-field>
        <p class="notice">{{ capability(call) }}</p>
      </div><mat-expansion-panel>
        <mat-expansion-panel-header><mat-panel-title>Advanced request behavior</mat-panel-title><mat-panel-description>Timeout, output, sampling, and retries</mat-panel-description></mat-expansion-panel-header>
        <div class="form-grid compact">
        <mat-form-field appearance="outline"><mat-label>Timeout · seconds</mat-label><input matInput type="number" [(ngModel)]="route(call).requestTimeoutSeconds"></mat-form-field>
        <mat-form-field appearance="outline"><mat-label>Maximum output tokens</mat-label><input matInput type="number" [(ngModel)]="route(call).maxOutputTokens"></mat-form-field>
        <mat-form-field appearance="outline"><mat-label>Temperature</mat-label><input matInput type="number" step=".1" [(ngModel)]="route(call).temperature"></mat-form-field>
        <mat-form-field appearance="outline"><mat-label>Top P</mat-label><input matInput type="number" step=".1" [(ngModel)]="route(call).topP"></mat-form-field>
        <mat-form-field appearance="outline"><mat-label>Reasoning effort</mat-label><input matInput [(ngModel)]="route(call).reasoningEffort" placeholder="low, medium, high"></mat-form-field>
        <mat-form-field appearance="outline"><mat-label>Maximum automatic retries</mat-label><input matInput type="number" [(ngModel)]="route(call).maxAutomaticRetries"></mat-form-field>
        <mat-form-field appearance="outline"><mat-label>Initial retry delay · seconds</mat-label><input matInput type="number" step=".25" [(ngModel)]="route(call).retryInitialDelaySeconds"></mat-form-field>
        <mat-form-field appearance="outline"><mat-label>Maximum retry delay · seconds</mat-label><input matInput type="number" [(ngModel)]="route(call).retryMaxDelaySeconds"></mat-form-field>
        <mat-form-field appearance="outline"><mat-label>Maximum Retry-After · seconds</mat-label><input matInput type="number" [(ngModel)]="route(call).retryMaxRetryAfterSeconds"></mat-form-field>
        </div>
      </mat-expansion-panel></mat-card-content></mat-card>
    }
    <div class="actions end"><button mat-flat-button (click)="save()">Save pipeline calls</button></div>
  `,
})
export class PipelineSettingsComponent implements OnInit {
  settings: AppSettings = defaultSettings();
  constructor(private readonly db: DbService, private readonly llm: LlmService, private readonly snack: MatSnackBar, private readonly changeDetector: ChangeDetectorRef) {}
  async ngOnInit(): Promise<void> { this.settings = await this.db.settings(); this.changeDetector.markForCheck(); }
  get selectedCalls(): GenerationCall[] { return this.callsForPipeline(this.settings.turnPipeline); }
  route(call: GenerationCall): GenerationCallRoute {
    return this.settings.generationCallRoutes[call] ??= {
      connectionId: this.settings.connections[0]?.id ?? '', modelId: this.settings.modelId,
      requestTimeoutSeconds: this.settings.requestTimeoutSeconds, maxOutputTokens: this.settings.maxOutputTokens,
      temperature: this.settings.temperature, topP: this.settings.topP, reasoningEffort: this.settings.reasoningEffort,
      maxAutomaticRetries: this.settings.maxAutomaticRetries, retryInitialDelaySeconds: this.settings.retryInitialDelaySeconds,
      retryMaxDelaySeconds: this.settings.retryMaxDelaySeconds, retryMaxRetryAfterSeconds: this.settings.retryMaxRetryAfterSeconds,
    };
  }
  label(call: GenerationCall): string { return call.replace(/([A-Z])/g, ' $1').replace(/^./, char => char.toUpperCase()); }
  capability(call: GenerationCall): string {
    const route = this.route(call); const connection = this.settings.connections.find(item => item.id === route.connectionId);
    const value = connection?.modelCapabilities?.[route.modelId];
    return value ? `Model capability: ${value.structuredOutputTier} (tested ${new Date(value.testedAtUtc).toLocaleString()})` : 'Model capability: untested — it will be tested when you save.';
  }
  async save(): Promise<void> {
    const errors = Object.values(validateSettings(this.settings)); if (errors.length) { this.snack.open(errors[0], 'Dismiss', { duration: 5000 }); return; }
    const unique = new Map<string, { connectionId: string; modelId: string }>();
    for (const call of this.selectedCalls) { const route = this.route(call); if (route.connectionId && route.modelId) unique.set(`${route.connectionId}:${route.modelId}`, route); }
    for (const route of unique.values()) {
      const connection = this.settings.connections.find(item => item.id === route.connectionId)!;
      if (connection.modelCapabilities?.[route.modelId]) continue;
      const result = await this.llm.test({ ...this.settings, baseUrl: connection.baseUrl, apiKey: connection.apiKey, modelId: route.modelId });
      connection.modelCapabilities ??= {};
      connection.modelCapabilities[route.modelId] = { structuredOutputTier: result.tier, outputTokenParameter: result.outputTokenParameter, instructionMessageRole: result.instructionMessageRole, testedAtUtc: new Date().toISOString() };
    }
    await this.db.saveSettings(this.settings); this.snack.open('Pipeline calls saved and selected models tested.', 'Dismiss', { duration: 2500 });
  }
  private callsForPipeline(pipeline: TurnPipelineMode): GenerationCall[] {
    switch (pipeline) {
      case 'oneCall': return ['storyDefinition', 'turn'];
      case 'twoCalls': return ['storyDefinition', 'narration', 'stateExtraction'];
      case 'threeCalls': return ['storyDefinition', 'adjudication', 'narration', 'stateExtraction'];
      case 'fourCalls': return ['storyDefinition', 'adjudication', 'scenePlan', 'narration', 'stateExtraction'];
      case 'fiveCalls': return ['storyDefinition', 'adjudication', 'scenePlan', 'planCritic', 'narration', 'stateExtraction'];
      case 'sevenCalls':
      case 'sevenCallsParallel': return ['storyDefinition', 'adjudication', 'scenePlan', 'narration', 'storyBibleAnalysis', 'plannedEventAnalysis', 'conditionSummaryAnalysis', 'stateExtraction'];
      case 'eightCalls': return ['storyDefinition', 'adjudication', 'scenePlan', 'narration', 'storyBibleAnalysis', 'plannedEventAnalysis', 'conditionSummaryAnalysis', 'stateExtraction', 'proseRevision'];
    }
  }
}
