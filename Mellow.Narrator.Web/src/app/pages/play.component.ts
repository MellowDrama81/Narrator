import { CommonModule } from '@angular/common';
import { ChangeDetectorRef, Component, DestroyRef, OnInit } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatSnackBar } from '@angular/material/snack-bar';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { DbService } from '../core/db.service';
import { defaultSettings } from '../core/defaults';
import { downloadJson, downloadText, safeFilename } from '../core/download';
import { nowIso, PlannedEvent, StoryBibleEntry, StoryCondition, StoryState, StoryTurn } from '../core/models';
import { OpenStoriesService } from '../core/open-stories.service';
import { NarratorService } from '../core/narrator.service';
import { validateBibleEntry } from '../core/story-bible';
import { BibleEditorComponent } from '../shared/bible-editor.component';
import { PlannedEventsEditorComponent } from '../shared/planned-events-editor.component';
import { ConditionsEditorComponent } from '../shared/conditions-editor.component';

@Component({
  imports: [
    CommonModule, FormsModule, RouterLink, MatButtonModule, MatCardModule, MatFormFieldModule,
    MatInputModule, MatProgressSpinnerModule, BibleEditorComponent, PlannedEventsEditorComponent, ConditionsEditorComponent,
  ],
  template: `
    @if (story) {
      <header class="page-header play-header">
        <div><a class="back-link" routerLink="/stories">← Stories</a><p class="eyebrow">Turn {{ story.turns.length }}</p><h1>{{ story.label }}</h1></div>
        <button mat-stroked-button (click)="toolsOpen = !toolsOpen">{{ toolsOpen ? 'Close tools' : 'Story tools' }}</button>
      </header>
      <div class="play-layout" [class.tools-open]="toolsOpen">
        <main class="narrative">
          @for (turn of story.turns; track turn.id) {
            @if (turn.playerAction) { <p class="player-action">{{ turn.playerAction }}</p> }
            <article class="turn" [class.opening]="turn.playerAction === null">
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
        @if (toolsOpen) {
          <aside class="story-tools">
            <div class="actions"><button mat-button (click)="export()">Export story</button><button mat-button (click)="exportHistory()">Export narration history</button><button mat-stroked-button [disabled]="culling" (click)="cullToLimits()">Cull to limits</button><button mat-stroked-button (click)="copy()">Copy story</button></div>
            <section class="summary-editor">
              <h2>Story state editor</h2>
              <mat-form-field appearance="outline" class="wide"><mat-label>Story label</mat-label><input matInput [(ngModel)]="story.label"></mat-form-field>
              <button mat-stroked-button (click)="saveLabel()">Save label</button>
              <h3>Story So Far</h3><p class="summary-hint">A compact recap the narrator rewrites every turn. Edit here only to correct drift from the actual story.</p>
              <mat-form-field appearance="outline" class="wide"><mat-label>Story So Far</mat-label><textarea matInput rows="12" [(ngModel)]="summaryDraft" [maxlength]="maxSummaryLength" placeholder="(empty until the opening scene establishes it)"></textarea><mat-hint>{{ summaryDraft.length }} / {{ maxSummaryLength }}</mat-hint></mat-form-field>
              <button mat-stroked-button (click)="saveSummary()">Save Summary</button>
              <app-bible-editor [(entries)]="story.currentStoryBible" (entriesChange)="saveBible($event)"></app-bible-editor>
              <app-planned-events-editor [(entries)]="story.currentPlannedEvents" (entriesChange)="savePlannedEvents($event)"></app-planned-events-editor>
              <app-conditions-editor [(entries)]="story.currentVictoryConditions" heading="Victory conditions" (entriesChange)="saveVictoryConditions($event)"></app-conditions-editor>
              <app-conditions-editor [(entries)]="story.currentLossConditions" heading="Loss conditions" (entriesChange)="saveLossConditions($event)"></app-conditions-editor>
            </section>
          </aside>
        }
      </div>
    }
  `,
  styles: [`
    .play-layout { display:grid; grid-template-columns:minmax(0, 760px); justify-content:center; gap:1.5rem; transition:.2s ease; }
    .play-layout.tools-open { grid-template-columns:1fr; max-width:100%; margin:auto; }
    .narrative { min-width:0; }
    .turn { padding:1.25rem 0 1.7rem; border-bottom:1px solid var(--line); }
    .player-action { color:var(--accent); font-weight:700; font-size:.82rem; letter-spacing:.04em; text-transform:uppercase; }
    .player-action::before { content:'YOU · '; color:var(--muted); }
    .player-action { margin:1rem 0 0; padding:.7rem .9rem; border-left:3px solid var(--accent-2); border-radius:10px; background:var(--soft); color:var(--text); font-style:italic; }
    .player-action::before { content:none; }
    .prose { font-family:var(--serif); font-size:clamp(1.08rem,1.7vw,1.28rem); line-height:1.78; margin:.75rem 0; }
    .turn-meta { color:var(--muted); font-size:.7rem; }
    .choice-box { position:sticky; bottom:1rem; z-index:3; margin:1.5rem 0; padding:1.1rem; background:color-mix(in srgb, var(--surface) 94%, transparent); backdrop-filter:blur(18px); border:1px solid var(--line); border-radius:20px; box-shadow:var(--shadow); }
    .suggestions { display:flex; flex-wrap:wrap; gap:.6rem; margin-bottom:.9rem; }
    .action-row { display:grid; grid-template-columns:1fr auto; gap:.8rem; align-items:center; }
    .action-row mat-form-field { width:100%; }
    .writing { min-height:100px; display:flex; align-items:center; justify-content:center; gap:1rem; }
    .writing div { display:flex; flex-direction:column; gap:.2rem; }
    .writing span { color:var(--muted); font-size:.82rem; }
    .story-tools { order:-1; width:100%; padding:1.5rem; border:1px solid color-mix(in srgb, var(--accent), var(--line)); border-radius:18px; background:color-mix(in srgb, var(--soft) 72%, var(--surface)); }
    .summary-editor { display:flex; flex-direction:column; gap:.6rem; }
    .summary-editor h3 { margin:0; }
    .summary-hint { color:var(--muted); font-size:.82rem; margin:0; }
    .history-actions { display:flex; justify-content:space-between; }
    @media (max-width:1000px) { .play-layout.tools-open { grid-template-columns:1fr; } }
    @media (max-width:600px) { .action-row{grid-template-columns:1fr}.choice-box{bottom:.4rem}.play-header{align-items:flex-start} }
  `],
})
export class PlayComponent implements OnInit {
  story?: StoryState;
  action = '';
  busy = false;
  culling = false;
  toolsOpen = false;
  summaryDraft = '';
  maxSummaryLength = defaultSettings().maxStorySummaryCharacters;
  private pendingKey = '';

  constructor(
    private readonly route: ActivatedRoute,
    private readonly router: Router,
    private readonly db: DbService,
    private readonly narrator: NarratorService,
    private readonly snack: MatSnackBar,
    private readonly changeDetector: ChangeDetectorRef,
    private readonly destroyRef: DestroyRef,
    private readonly openStories: OpenStoriesService,
  ) {}

  ngOnInit(): void {
    // /stories/:id resolves to this same component instance across navigations within this route
    // config (e.g. copy() navigating from the source story's id to the new copy's id) - the Router
    // reuses the instance and does not re-run ngOnInit just because the id param changed. Subscribing
    // to paramMap, instead of reading route.snapshot.paramMap once, is what makes the view actually
    // switch to the new story instead of silently continuing to show the old one.
    this.route.paramMap.pipe(takeUntilDestroyed(this.destroyRef)).subscribe(params => {
      void this.load(params.get('id')!);
    });
  }

  private async load(id: string): Promise<void> {
    this.story = await this.db.story(id);
    if (!this.story) { await this.router.navigate(['/stories']); return; }
    await this.openStories.open(id);
    this.toolsOpen = false;
    this.pendingKey = `pending-action-${id}`;
    this.action = await this.db.meta<string>(this.pendingKey) ?? '';
    this.summaryDraft = this.story.storySummary;
    this.maxSummaryLength = (await this.db.settings()).maxStorySummaryCharacters;
    this.changeDetector.markForCheck();
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
      this.summaryDraft = this.story.storySummary;
      await this.db.saveMeta(this.pendingKey, '');
      this.notifyMetConditions(this.story.turns.at(-1)!);
      setTimeout(() => window.scrollTo({ top: document.body.scrollHeight, behavior: 'smooth' }));
    } catch (error) {
      this.snack.open(error instanceof Error ? error.message : 'The story request failed.', 'Dismiss', { duration: 8000 });
    } finally {
      this.busy = false;
      this.changeDetector.markForCheck();
    }
  }

  // Only ever called right after narrator.play() resolves, looking at that just-produced turn's own
  // delta fields (never cumulative) - so a condition met in an earlier turn never surfaces this notice
  // again. The narration prose is expected to already read naturally for a revealed condition; only a
  // freshly met one gets a dedicated notice here.
  private notifyMetConditions(turn: StoryTurn): void {
    if (!this.story) return;
    const metIds = [...turn.metVictoryConditionIds, ...turn.metLossConditionIds];
    if (!metIds.length) return;
    const all = [...this.story.currentVictoryConditions, ...this.story.currentLossConditions];
    const descriptions = metIds
      .map(id => all.find(condition => condition.id === id)?.description)
      .filter((description): description is string => !!description);
    if (!descriptions.length) return;
    this.snack.open(`Condition met: ${descriptions.join(' · ')}`, 'Keep playing', { duration: 15000 });
  }

  async saveBible(entries: StoryBibleEntry[]): Promise<void> {
    if (!this.story) return;
    const bible = entries.map(entry => ({
      ...entry, name: entry.name.trim(), category: entry.category.trim(),
      knownFacts: entry.knownFacts.map(x => x.trim()),
      secretFacts: entry.secretFacts.map(x => x.trim()),
    }));
    const bibleError = bible.map(validateBibleEntry).find(Boolean);
    if (bibleError) { this.snack.open(bibleError, 'Dismiss', { duration: 7000 }); return; }
    this.story.currentStoryBible = bible;
    await this.db.saveStory(this.story);
  }

  async savePlannedEvents(entries: PlannedEvent[]): Promise<void> {
    if (!this.story) return;
    this.story.currentPlannedEvents = entries;
    await this.db.saveStory(this.story);
  }

  async saveVictoryConditions(entries: StoryCondition[]): Promise<void> {
    if (!this.story) return;
    this.story.currentVictoryConditions = entries.filter(entry => entry.description.trim()).map(entry => ({ ...entry, description: entry.description.trim() }));
    const ids = new Set(this.story.currentVictoryConditions.map(entry => entry.id));
    this.story.revealedVictoryConditionIds = this.story.revealedVictoryConditionIds.filter(id => ids.has(id));
    this.story.metVictoryConditionIds = this.story.metVictoryConditionIds.filter(id => ids.has(id));
    await this.db.saveStory(this.story);
  }

  async saveLossConditions(entries: StoryCondition[]): Promise<void> {
    if (!this.story) return;
    this.story.currentLossConditions = entries.filter(entry => entry.description.trim()).map(entry => ({ ...entry, description: entry.description.trim() }));
    const ids = new Set(this.story.currentLossConditions.map(entry => entry.id));
    this.story.revealedLossConditionIds = this.story.revealedLossConditionIds.filter(id => ids.has(id));
    this.story.metLossConditionIds = this.story.metLossConditionIds.filter(id => ids.has(id));
    await this.db.saveStory(this.story);
  }

  async saveLabel(): Promise<void> {
    if (!this.story) return;
    this.story.label = this.story.label.trim() || this.story.definition.title;
    await this.db.saveStory(this.story);
    this.openStories.notifyChanged();
    this.snack.open('Label saved.', 'Dismiss', { duration: 2500 });
  }

  // Manual override for the narrator-maintained Story So Far recap - mirrors PlayStoryPage's
  // BuildSummaryEditor/SaveStorySummaryAsync in the MAUI app. The narrator rewrites this every turn, so
  // this exists mainly as a safety valve to correct drift from the actual story.
  async saveSummary(): Promise<void> {
    if (!this.story) return;
    try {
      this.story = await this.narrator.updateStorySummary(this.story.id, this.summaryDraft);
      this.summaryDraft = this.story.storySummary;
      this.snack.open('Story summary saved.', 'Dismiss', { duration: 3000 });
    } catch (error) {
      this.snack.open(error instanceof Error ? error.message : 'The story summary could not be saved.', 'Dismiss', { duration: 7000 });
    }
  }

  // Trims the current Story Bible/Planned Events down to the currently configured limits - mirrors
  // NarratorApplication.CullStoryStateAsync. Confirms first, then reports exactly what was removed
  // (rather than culling silently), diffing the before/after entry ids since cullStoryState() itself
  // returns only the updated Story State.
  async cullToLimits(): Promise<void> {
    if (!this.story) return;
    if (!confirm('Cull the Story Bible and Planned Events down to the currently configured limits? Lower-importance or less-recently-relevant entries may be removed.')) return;
    this.culling = true;
    try {
      const before = this.story;
      const after = await this.narrator.cullStoryState(before);
      this.story = after;
      this.reportCulled(before.currentStoryBible, after.currentStoryBible, before.currentPlannedEvents, after.currentPlannedEvents);
    } catch (error) {
      this.snack.open(error instanceof Error ? error.message : 'The cull request failed.', 'Dismiss', { duration: 8000 });
    } finally {
      this.culling = false;
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
    await this.openStories.close(this.story.id);
    await this.router.navigate(['/stories']);
  }
}
