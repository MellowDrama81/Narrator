import { CommonModule } from '@angular/common';
import { Component, EventEmitter, Input, Output } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MatChipsModule } from '@angular/material/chips';
import { MatExpansionModule } from '@angular/material/expansion';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { PlannedEvent, uuid } from '../core/models';
import { MANDATORY_IMPORTANCE } from '../core/planned-events';

@Component({
  selector: 'app-planned-events-editor',
  imports: [
    CommonModule, FormsModule, MatButtonModule, MatCheckboxModule, MatChipsModule, MatExpansionModule,
    MatFormFieldModule, MatInputModule, MatSelectModule,
  ],
  template: `
    <section class="planned-events">
      <div class="planned-events-tools">
        <div>
          <p class="eyebrow">Secret plans</p>
          <h2>Planned Events <span>{{ entries.length }}</span></h2>
        </div>
        <div class="filters">
          <mat-form-field appearance="outline" subscriptSizing="dynamic">
            <mat-label>Search description</mat-label>
            <input matInput [(ngModel)]="search">
          </mat-form-field>
          <mat-form-field appearance="outline" subscriptSizing="dynamic">
            <mat-label>Importance</mat-label>
            <mat-select [(ngModel)]="importance">
              <mat-option [value]="0">All</mat-option>
              @for (level of [5,4,3,2,1]; track level) { <mat-option [value]="level">{{ level }}</mat-option> }
            </mat-select>
          </mat-form-field>
          @if (editable) { <button mat-flat-button (click)="add()">Add event</button> }
        </div>
      </div>

      @if (!filtered.length) {
        <div class="empty-mini">No Planned Events match this view.</div>
      } @else {
        <mat-accordion multi>
          @for (entry of filtered; track entry.id) {
            <mat-expansion-panel>
              <mat-expansion-panel-header>
                <mat-panel-title>{{ summarize(entry.description) }}</mat-panel-title>
                <mat-panel-description>
                  <mat-chip-set>
                    <mat-chip>{{ entry.importance === MANDATORY_IMPORTANCE ? 'Mandatory' : 'Importance ' + entry.importance }}</mat-chip>
                    <mat-chip>Urgency {{ entry.urgency }}</mat-chip>
                    @if (pendingPrerequisites(entry) > 0) { <mat-chip>{{ pendingPrerequisites(entry) }} pending</mat-chip> }
                  </mat-chip-set>
                </mat-panel-description>
              </mat-expansion-panel-header>
              <div class="entry-grid">
                <mat-form-field appearance="outline" class="description">
                  <mat-label>Description</mat-label>
                  <textarea matInput rows="3" [(ngModel)]="entry.description" [disabled]="!editable" (ngModelChange)="changed()"></textarea>
                </mat-form-field>
                <mat-form-field appearance="outline">
                  <mat-label>Importance</mat-label>
                  <mat-select [(ngModel)]="entry.importance" [disabled]="!editable" (ngModelChange)="changed()">
                    @for (level of [1,2,3,4,5]; track level) { <mat-option [value]="level">{{ level }}</mat-option> }
                  </mat-select>
                  <mat-hint>5 is mandatory: the narrator must force it to happen</mat-hint>
                </mat-form-field>
                <mat-form-field appearance="outline">
                  <mat-label>Urgency</mat-label>
                  <mat-select [(ngModel)]="entry.urgency" [disabled]="!editable" (ngModelChange)="changed()">
                    @for (level of [1,2,3,4,5]; track level) { <mat-option [value]="level">{{ level }}</mat-option> }
                  </mat-select>
                  <mat-hint>5 = steer toward it now; 1 = let it emerge naturally</mat-hint>
                </mat-form-field>
                <span class="relevance">Last relevant turn {{ entry.lastRelevantTurnNumber }}</span>
                <div class="prerequisites">
                  <p class="prereq-label">Prerequisites &middot; must occur before this is pursued</p>
                  @if (candidates(entry).length === 0) {
                    <span class="no-candidates">No other Planned Events to depend on yet.</span>
                  } @else {
                    @for (candidate of candidates(entry); track candidate.id) {
                      <mat-checkbox
                        [disabled]="!editable"
                        [checked]="entry.prerequisiteEventIds.includes(candidate.id)"
                        (change)="togglePrerequisite(entry, candidate.id, $event.checked)">
                        {{ summarize(candidate.description) }}
                      </mat-checkbox>
                    }
                  }
                </div>
              </div>
              @if (editable) {
                <button mat-button class="danger" (click)="remove(entry)">Remove event</button>
              }
            </mat-expansion-panel>
          }
        </mat-accordion>
      }
    </section>
  `,
  styles: [`
    .planned-events { margin-top: 2rem; }
    .planned-events-tools { display:flex; align-items:end; justify-content:space-between; gap:1rem; margin-bottom:1rem; }
    h2 { margin:.15rem 0 0; font-family:var(--serif); font-size:clamp(1.5rem,3vw,2rem); }
    h2 span { color:var(--muted); font-family:var(--sans); font-size:.9rem; }
    .filters { display:flex; align-items:center; flex-wrap:wrap; gap:.65rem; }
    .entry-grid { display:grid; grid-template-columns:1fr 160px 160px; gap:.8rem; align-items:start; padding-top:.75rem; }
    .description { grid-column:span 3; }
    .relevance { color:var(--muted); font-size:.8rem; align-self:center; }
    .prerequisites { grid-column:span 3; display:flex; flex-direction:column; gap:.3rem; }
    .prereq-label { color:var(--muted); font-size:.78rem; margin:0 0 .2rem; }
    .no-candidates { color:var(--muted); font-size:.8rem; }
    .danger { color:var(--danger) !important; }
    .empty-mini { padding:1.5rem; border:1px dashed var(--line); border-radius:16px; color:var(--muted); }
    mat-expansion-panel { margin-bottom:.7rem; border:1px solid var(--line); box-shadow:none !important; }
    mat-chip { font-size:.72rem; }
    @media (max-width: 800px) {
      .planned-events-tools { align-items:stretch; flex-direction:column; }
      .entry-grid { grid-template-columns:1fr; }
      .description, .prerequisites { grid-column:auto; }
      mat-panel-description { display:none; }
    }
  `],
})
export class PlannedEventsEditorComponent {
  @Input({ required: true }) entries: PlannedEvent[] = [];
  @Input() editable = true;
  @Output() entriesChange = new EventEmitter<PlannedEvent[]>();
  search = '';
  importance = 0;
  readonly MANDATORY_IMPORTANCE = MANDATORY_IMPORTANCE;

  get filtered(): PlannedEvent[] {
    const needle = this.search.trim().toLowerCase();
    return this.entries.filter(entry =>
      (!this.importance || entry.importance === this.importance) &&
      (!needle || entry.description.toLowerCase().includes(needle)));
  }

  candidates(entry: PlannedEvent): PlannedEvent[] {
    return this.entries.filter(other => other.id !== entry.id);
  }

  pendingPrerequisites(entry: PlannedEvent): number {
    const liveIds = new Set(this.entries.map(other => other.id));
    return entry.prerequisiteEventIds.filter(id => liveIds.has(id)).length;
  }

  togglePrerequisite(entry: PlannedEvent, candidateId: string, checked: boolean): void {
    entry.prerequisiteEventIds = checked
      ? [...entry.prerequisiteEventIds, candidateId]
      : entry.prerequisiteEventIds.filter(id => id !== candidateId);
    this.changed();
  }

  add(): void {
    this.entries = [...this.entries, {
      id: uuid(), description: 'New planned event', importance: 3, urgency: 3,
      prerequisiteEventIds: [], lastRelevantTurnNumber: 0,
    }];
    this.changed();
  }

  remove(entry: PlannedEvent): void {
    const dependents = this.entries.filter(other => other.id !== entry.id && other.prerequisiteEventIds.includes(entry.id));
    const mandatoryNotice = entry.importance === MANDATORY_IMPORTANCE ? ' This is a mandatory Planned Event.' : '';
    const dependentsNotice = dependents.length
      ? ` This is a prerequisite for: ${dependents.map(x => this.summarize(x.description)).join(', ')}.`
      : '';
    if (!confirm(`Remove “${this.summarize(entry.description)}” from Planned Events?${mandatoryNotice}${dependentsNotice}`)) return;
    this.entries = this.entries.filter(value => value.id !== entry.id);
    this.changed();
  }

  summarize(description: string): string {
    return description.length <= 80 ? description : `${description.slice(0, 80)}…`;
  }

  changed(): void { this.entriesChange.emit(this.entries); }
}
