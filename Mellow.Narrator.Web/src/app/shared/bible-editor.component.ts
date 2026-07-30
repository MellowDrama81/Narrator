import { CommonModule } from '@angular/common';
import { Component, EventEmitter, Input, Output } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatChipsModule } from '@angular/material/chips';
import { MatExpansionModule } from '@angular/material/expansion';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { StoryBibleEntry, uuid } from '../core/models';

@Component({
  selector: 'app-bible-editor',
  imports: [
    CommonModule, FormsModule, MatButtonModule, MatChipsModule, MatExpansionModule,
    MatFormFieldModule, MatInputModule, MatSelectModule,
  ],
  template: `
    <section class="bible">
      <div class="bible-tools">
        <div>
          <p class="eyebrow">Structured memory</p>
          <h2>Story Bible <span>{{ entries.length }}</span></h2>
        </div>
        <div class="filters">
          <mat-form-field appearance="outline" subscriptSizing="dynamic">
            <mat-label>Search entries</mat-label>
            <input matInput [(ngModel)]="search">
          </mat-form-field>
          <mat-form-field appearance="outline" subscriptSizing="dynamic">
            <mat-label>Importance</mat-label>
            <mat-select [(ngModel)]="importance">
              <mat-option [value]="0">All</mat-option>
              @for (level of [5,4,3,2,1]; track level) { <mat-option [value]="level">{{ level }}</mat-option> }
            </mat-select>
          </mat-form-field>
          @if (editable) { <button mat-flat-button (click)="add()">Add entry</button> }
        </div>
      </div>

      @if (!filtered.length) {
        <div class="empty-mini">No Story Bible entries match this view.</div>
      } @else {
        <mat-accordion multi>
          @for (entry of filtered; track entry.id) {
            <mat-expansion-panel>
              <mat-expansion-panel-header>
                <mat-panel-title>{{ entry.name }}</mat-panel-title>
                <mat-panel-description>
                  <mat-chip-set><mat-chip>{{ entry.category || 'Uncategorized' }}</mat-chip><mat-chip>Importance {{ entry.importance }}</mat-chip></mat-chip-set>
                </mat-panel-description>
              </mat-expansion-panel-header>
              <div class="entry-grid">
                <mat-form-field appearance="outline">
                  <mat-label>Name</mat-label>
                  <input matInput [(ngModel)]="entry.name" [disabled]="!editable" (ngModelChange)="changed()">
                </mat-form-field>
                <mat-form-field appearance="outline">
                  <mat-label>Category</mat-label>
                  <input matInput [(ngModel)]="entry.category" [disabled]="!editable" (ngModelChange)="changed()">
                </mat-form-field>
                <mat-form-field appearance="outline">
                  <mat-label>Importance</mat-label>
                  <mat-select [(ngModel)]="entry.importance" [disabled]="!editable" (ngModelChange)="changed()">
                    @for (level of [1,2,3,4,5]; track level) { <mat-option [value]="level">{{ level }}</mat-option> }
                  </mat-select>
                </mat-form-field>
                <span class="relevance">Last relevant turn {{ entry.lastRelevantTurnNumber }}</span>
                <mat-form-field appearance="outline" class="facts">
                  <mat-label>Known facts · one per line</mat-label>
                  <textarea matInput rows="5" [ngModel]="entry.knownFacts.join('\\n')" [disabled]="!editable" (ngModelChange)="facts(entry, 'knownFacts', $event)"></textarea>
                </mat-form-field>
                <mat-form-field appearance="outline" class="facts secret">
                  <mat-label>Secret facts · one per line</mat-label>
                  <textarea matInput rows="5" [ngModel]="entry.secretFacts.join('\\n')" [disabled]="!editable" (ngModelChange)="facts(entry, 'secretFacts', $event)"></textarea>
                </mat-form-field>
              </div>
              @if (editable) {
                <button mat-button class="danger" (click)="remove(entry)">Remove entry</button>
              }
            </mat-expansion-panel>
          }
        </mat-accordion>
      }
    </section>
  `,
  styles: [`
    .bible { margin-top: 2rem; }
    .bible-tools { display:flex; align-items:end; justify-content:space-between; gap:1rem; margin-bottom:1rem; }
    h2 { margin:.15rem 0 0; font-family:var(--serif); font-size:clamp(1.5rem,3vw,2rem); }
    h2 span { color:var(--muted); font-family:var(--sans); font-size:.9rem; }
    .filters { display:flex; align-items:center; flex-wrap:wrap; gap:.65rem; }
    .entry-grid { display:grid; grid-template-columns:1fr 1fr 160px auto; gap:.8rem; align-items:center; padding-top:.75rem; }
    .facts { grid-column:span 2; }
    .relevance { color:var(--muted); font-size:.8rem; }
    .secret textarea { color:#7b2f5d; }
    .danger { color:var(--danger) !important; }
    .empty-mini { padding:1.5rem; border:1px dashed var(--line); border-radius:16px; color:var(--muted); }
    mat-expansion-panel { margin-bottom:.7rem; border:1px solid var(--line); box-shadow:none !important; }
    mat-chip { font-size:.72rem; }
    @media (max-width: 800px) {
      .bible-tools { align-items:stretch; flex-direction:column; }
      .entry-grid { grid-template-columns:1fr; }
      .facts { grid-column:auto; }
      mat-panel-description { display:none; }
    }
  `],
})
export class BibleEditorComponent {
  @Input({ required: true }) entries: StoryBibleEntry[] = [];
  @Input() editable = true;
  @Output() entriesChange = new EventEmitter<StoryBibleEntry[]>();
  search = '';
  importance = 0;

  get filtered(): StoryBibleEntry[] {
    const needle = this.search.trim().toLowerCase();
    return this.entries.filter(entry =>
      (!this.importance || entry.importance === this.importance) &&
      (!needle || `${entry.category} ${entry.name} ${entry.knownFacts.join(' ')} ${entry.secretFacts.join(' ')}`.toLowerCase().includes(needle)));
  }

  add(): void {
    this.entries = [...this.entries, {
      id: uuid(), category: 'Character', name: 'New entry', knownFacts: ['Add a known fact'],
      secretFacts: [], importance: 3, lastRelevantTurnNumber: 0,
    }];
    this.changed();
  }

  remove(entry: StoryBibleEntry): void {
    if (!confirm(`Remove “${entry.name}” from the Story Bible?`)) return;
    this.entries = this.entries.filter(value => value.id !== entry.id);
    this.changed();
  }

  facts(entry: StoryBibleEntry, field: 'knownFacts' | 'secretFacts', value: string): void {
    entry[field] = value.split(/\r?\n/).map(x => x.trim()).filter(Boolean);
    this.changed();
  }

  changed(): void { this.entriesChange.emit(this.entries); }
}

