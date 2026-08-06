import { CommonModule } from '@angular/common';
import { Component, EventEmitter, Input, Output } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { StoryCondition, uuid } from '../core/models';

// A simpler analog of PlannedEventsEditorComponent: a Story Condition is just a description and a
// secret flag - no importance/urgency/prerequisites, and the narrator never adds, replaces, or removes
// one during play (see the StoryCondition doc comment in models.ts), so there's no filtering or
// relationship UI to build here. One component instance serves both Victory and Loss Conditions,
// distinguished only by the heading passed in.
@Component({
  selector: 'app-conditions-editor',
  imports: [CommonModule, FormsModule, MatButtonModule, MatCheckboxModule, MatFormFieldModule, MatInputModule],
  template: `
    <section class="conditions">
      <div class="conditions-tools">
        <div>
          <p class="eyebrow">Fixed outcomes</p>
          <h2>{{ heading }} <span>{{ entries.length }}</span></h2>
        </div>
        @if (editable) { <button mat-flat-button (click)="add()">Add condition</button> }
      </div>

      @if (!entries.length) {
        <div class="empty-mini">No {{ heading }} defined.</div>
      } @else {
        @for (entry of entries; track entry.id) {
          <div class="condition-row">
            <mat-form-field appearance="outline" class="description">
              <mat-label>Description</mat-label>
              <textarea matInput rows="2" [(ngModel)]="entry.description" [disabled]="!editable" (ngModelChange)="changed()"></textarea>
            </mat-form-field>
            <mat-checkbox [(ngModel)]="entry.secret" [disabled]="!editable" (ngModelChange)="changed()">
              Secret
            </mat-checkbox>
            @if (editable) {
              <button mat-button class="danger" (click)="remove(entry)">Remove</button>
            }
          </div>
        }
      }
    </section>
  `,
  styles: [`
    .conditions { margin-top: 2rem; }
    .conditions-tools { display:flex; align-items:end; justify-content:space-between; gap:1rem; margin-bottom:1rem; }
    h2 { margin:.15rem 0 0; font-family:var(--serif); font-size:clamp(1.5rem,3vw,2rem); }
    h2 span { color:var(--muted); font-family:var(--sans); font-size:.9rem; }
    .condition-row { display:grid; grid-template-columns:1fr auto auto; gap:.8rem; align-items:start; padding:.75rem 0; border-bottom:1px solid var(--line); }
    .condition-row:last-of-type { border-bottom:0; }
    .description { grid-column:1; }
    mat-checkbox { margin-top:.9rem; }
    .danger { color:var(--danger) !important; margin-top:.4rem; }
    .empty-mini { padding:1.5rem; border:1px dashed var(--line); border-radius:16px; color:var(--muted); }
    @media (max-width: 800px) {
      .conditions-tools { align-items:stretch; flex-direction:column; }
      .condition-row { grid-template-columns:1fr; }
    }
  `],
})
export class ConditionsEditorComponent {
  @Input({ required: true }) entries: StoryCondition[] = [];
  @Input() editable = true;
  @Input({ required: true }) heading = '';
  @Output() entriesChange = new EventEmitter<StoryCondition[]>();

  add(): void {
    this.entries = [...this.entries, { id: uuid(), description: 'New condition', secret: false }];
    this.changed();
  }

  remove(entry: StoryCondition): void {
    if (!confirm(`Remove “${this.summarize(entry.description)}” from ${this.heading}?`)) return;
    this.entries = this.entries.filter(value => value.id !== entry.id);
    this.changed();
  }

  summarize(description: string): string {
    return description.length <= 80 ? description : `${description.slice(0, 80)}…`;
  }

  changed(): void { this.entriesChange.emit(this.entries); }
}
