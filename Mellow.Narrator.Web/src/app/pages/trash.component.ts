import { CommonModule } from '@angular/common';
import { Component, OnInit } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatChipsModule } from '@angular/material/chips';
import { MatSnackBar } from '@angular/material/snack-bar';
import { RouterLink } from '@angular/router';
import { DbService } from '../core/db.service';
import { TrashItem } from '../core/models';
import { NarratorService } from '../core/narrator.service';

@Component({
  imports: [CommonModule, RouterLink, MatButtonModule, MatCardModule, MatChipsModule],
  template: `
    <header class="page-header">
      <div><a class="back-link" routerLink="/settings">← Settings</a><p class="eyebrow">Recover or remove</p><h1>Trash</h1></div>
      @if (items.length) { <button mat-stroked-button class="danger" (click)="empty()">Empty trash</button> }
    </header>
    @if (!items.length) {
      <div class="empty-state"><span class="empty-mark">0</span><h2>Trash is empty.</h2><p>Deleted Story Definitions and Stories will appear here until permanently removed.</p></div>
    } @else {
      <div class="list-stack">
        @for (item of items; track item.trashId) {
          <mat-card class="row-card">
            <mat-card-content>
              <div><mat-chip>{{ item.type === 'definition' ? 'Definition' : 'Story' }}</mat-chip><h2>{{ item.displayName }}</h2><p>Deleted {{ item.deletedAtUtc | date:'medium' }}</p></div>
              <div class="actions"><button mat-flat-button (click)="restore(item)">Restore</button><button mat-button class="danger" (click)="permanent(item)">Delete permanently</button></div>
            </mat-card-content>
          </mat-card>
        }
      </div>
    }
  `,
})
export class TrashComponent implements OnInit {
  items: TrashItem[] = [];
  constructor(private readonly db: DbService, private readonly narrator: NarratorService, private readonly snack: MatSnackBar) {}
  async ngOnInit(): Promise<void> { await this.reload(); }

  async restore(item: TrashItem): Promise<void> {
    await this.narrator.restore(item);
    await this.reload();
    this.snack.open('Item restored.', 'Dismiss', { duration: 2500 });
  }

  async permanent(item: TrashItem): Promise<void> {
    if (!confirm(`Permanently delete “${item.displayName}”? This cannot be undone.`)) return;
    await this.db.deleteTrash(item.trashId);
    await this.reload();
  }

  async empty(): Promise<void> {
    if (!confirm('Permanently delete everything in Trash?')) return;
    await Promise.all(this.items.map(item => this.db.deleteTrash(item.trashId)));
    await this.reload();
  }

  private async reload(): Promise<void> {
    this.items = (await this.db.trash()).sort((a, b) => b.deletedAtUtc.localeCompare(a.deletedAtUtc));
  }
}

