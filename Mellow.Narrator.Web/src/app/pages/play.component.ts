import { CommonModule } from '@angular/common';
import { Component, OnInit } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatSnackBar } from '@angular/material/snack-bar';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { DbService } from '../core/db.service';
import { downloadJson, downloadText, safeFilename } from '../core/download';
import { nowIso, StoryBibleEntry, StoryState } from '../core/models';
import { NarratorService } from '../core/narrator.service';
import { BibleEditorComponent } from '../shared/bible-editor.component';

@Component({
  imports: [
    CommonModule, FormsModule, RouterLink, MatButtonModule, MatCardModule, MatFormFieldModule,
    MatInputModule, MatProgressSpinnerModule, BibleEditorComponent,
  ],
  template: `
    @if (story) {
      <header class="page-header play-header">
        <div><a class="back-link" routerLink="/stories">← Stories</a><p class="eyebrow">Turn {{ story.turns.length }}</p><h1>{{ story.label }}</h1></div>
        <div class="actions">
          <button mat-button (click)="bibleOpen = !bibleOpen">{{ bibleOpen ? 'Hide' : 'Open' }} Story Bible</button>
          <button mat-stroked-button (click)="copy()">Copy story</button>
          <button mat-button (click)="export()">Export</button>
        </div>
      </header>
      <div class="play-layout" [class.bible-open]="bibleOpen">
        <main class="narrative">
          @for (turn of story.turns; track turn.id) {
            <article class="turn" [class.opening]="turn.playerAction === null">
              @if (turn.playerAction) { <p class="player-action">{{ turn.playerAction }}</p> }
              @for (paragraph of paragraphs(turn.narration); track $index) { <p class="prose">{{ paragraph }}</p> }
              <span class="turn-meta">{{ turn.modelId }} · {{ turn.completedAtUtc | date:'short' }}</span>
            </article>
          }

          <section class="choice-box">
            @if (busy) {
              <div class="writing"><mat-spinner diameter="34"></mat-spinner><div><strong>Writing the next scene…</strong><span>The completed turn will be saved before this view updates.</span></div></div>
            } @else {
              <div class="suggestions">
                @for (suggestion of suggestions; track suggestion) {
                  <button mat-stroked-button (click)="submit(suggestion)">{{ suggestion }}</button>
                }
              </div>
              <div class="action-row">
                <mat-form-field appearance="outline">
                  <mat-label>What do you do?</mat-label>
                  <textarea matInput rows="3" [(ngModel)]="action" (ngModelChange)="savePending()" (keydown.control.enter)="submit()"></textarea>
                  <mat-hint>Ctrl + Enter to submit</mat-hint>
                </mat-form-field>
                <button mat-flat-button [disabled]="!action.trim()" (click)="submit()">Continue</button>
              </div>
            }
          </section>
          <div class="history-actions">
            <button mat-button (click)="exportHistory()">Export full history</button>
            <button mat-button class="danger" (click)="remove()">Move story to trash</button>
          </div>
        </main>
        @if (bibleOpen) {
          <aside class="bible-panel">
            <app-bible-editor [(entries)]="story.currentStoryBible" (entriesChange)="saveBible($event)"></app-bible-editor>
          </aside>
        }
      </div>
    }
  `,
  styles: [`
    .play-layout { display:grid; grid-template-columns:minmax(0, 760px); justify-content:center; gap:1.5rem; transition:.2s ease; }
    .play-layout.bible-open { grid-template-columns:minmax(0, 1.7fr) minmax(340px, 1fr); max-width:1400px; margin:auto; }
    .narrative { min-width:0; }
    .turn { padding:1.25rem 0 1.7rem; border-bottom:1px solid var(--line); }
    .player-action { color:var(--accent); font-weight:700; font-size:.82rem; letter-spacing:.04em; text-transform:uppercase; }
    .player-action::before { content:'YOU · '; color:var(--muted); }
    .prose { font-family:var(--serif); font-size:clamp(1.08rem,1.7vw,1.28rem); line-height:1.78; margin:.75rem 0; }
    .turn-meta { color:var(--muted); font-size:.7rem; }
    .choice-box { position:sticky; bottom:1rem; z-index:3; margin:1.5rem 0; padding:1.1rem; background:color-mix(in srgb, var(--surface) 94%, transparent); backdrop-filter:blur(18px); border:1px solid var(--line); border-radius:20px; box-shadow:var(--shadow); }
    .suggestions { display:flex; flex-wrap:wrap; gap:.6rem; margin-bottom:.9rem; }
    .action-row { display:grid; grid-template-columns:1fr auto; gap:.8rem; align-items:center; }
    .action-row mat-form-field { width:100%; }
    .writing { min-height:100px; display:flex; align-items:center; justify-content:center; gap:1rem; }
    .writing div { display:flex; flex-direction:column; gap:.2rem; }
    .writing span { color:var(--muted); font-size:.82rem; }
    .bible-panel { border-left:1px solid var(--line); padding-left:1.5rem; min-width:0; }
    .history-actions { display:flex; justify-content:space-between; }
    @media (max-width:1000px) { .play-layout.bible-open { grid-template-columns:1fr; }.bible-panel{border-left:0;padding-left:0;border-top:1px solid var(--line)} }
    @media (max-width:600px) { .action-row{grid-template-columns:1fr}.choice-box{bottom:.4rem}.play-header{align-items:flex-start} }
  `],
})
export class PlayComponent implements OnInit {
  story?: StoryState;
  action = '';
  busy = false;
  bibleOpen = false;
  private pendingKey = '';

  constructor(
    private readonly route: ActivatedRoute,
    private readonly router: Router,
    private readonly db: DbService,
    private readonly narrator: NarratorService,
    private readonly snack: MatSnackBar,
  ) {}

  async ngOnInit(): Promise<void> {
    const id = this.route.snapshot.paramMap.get('id')!;
    this.story = await this.db.story(id);
    if (!this.story) { await this.router.navigate(['/stories']); return; }
    this.pendingKey = `pending-action-${id}`;
    this.action = await this.db.meta<string>(this.pendingKey) ?? '';
  }

  get suggestions(): string[] { return this.story?.turns.at(-1)?.suggestedActions ?? []; }
  paragraphs(value: string): string[] { return value.split(/\n\s*\n|\r?\n/).map(x => x.trim()).filter(Boolean); }
  savePending(): void { void this.db.saveMeta(this.pendingKey, this.action); }

  async submit(suggestion?: string): Promise<void> {
    if (!this.story) return;
    const value = (suggestion ?? this.action).trim();
    if (!value) return;
    this.busy = true;
    try {
      this.story = await this.narrator.play(this.story.id, value);
      this.action = '';
      await this.db.saveMeta(this.pendingKey, '');
      setTimeout(() => window.scrollTo({ top: document.body.scrollHeight, behavior: 'smooth' }));
    } catch (error) {
      this.snack.open(error instanceof Error ? error.message : 'The story request failed.', 'Dismiss', { duration: 8000 });
    } finally { this.busy = false; }
  }

  async saveBible(entries: StoryBibleEntry[]): Promise<void> {
    if (!this.story) return;
    this.story.currentStoryBible = entries;
    await this.db.saveStory(this.story);
  }

  async copy(): Promise<void> {
    if (!this.story) return;
    const copy = await this.narrator.copyStory(this.story);
    await this.router.navigate(['/stories', copy.id]);
  }

  export(): void {
    if (!this.story) return;
    downloadJson(`${safeFilename(this.story.label)}-story.json`, { formatVersion: 1, exportedAtUtc: nowIso(), story: this.story });
  }

  exportHistory(): void {
    if (!this.story) return;
    const text = this.story.turns.map(turn =>
      `${turn.playerAction ? `YOU\n${turn.playerAction}\n\n` : ''}${turn.narration}`).join('\n\n— — —\n\n');
    downloadText(`${safeFilename(this.story.label)}-history.txt`, text);
  }

  async remove(): Promise<void> {
    if (!this.story || !confirm(`Move “${this.story.label}” to Trash?`)) return;
    await this.narrator.trashStory(this.story);
    await this.router.navigate(['/stories']);
  }
}

