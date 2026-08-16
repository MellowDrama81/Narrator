import { Component, OnInit } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { NavigationEnd, Router, RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { filter } from 'rxjs';
import { DbService } from './core/db.service';
import { OpenStoriesService } from './core/open-stories.service';
import { StoryState } from './core/models';

@Component({
  selector: 'app-root',
  imports: [RouterOutlet, RouterLink, RouterLinkActive, FormsModule, MatButtonModule],
  templateUrl: './app.html',
  styleUrl: './app.scss'
})
export class App implements OnInit {
  openStories: StoryState[] = [];
  renamingId = '';
  renameValue = '';

  constructor(private readonly openStoryWorkspace: OpenStoriesService, private readonly db: DbService, private readonly router: Router) {}

  async ngOnInit(): Promise<void> {
    await this.refreshOpenStories();
    this.router.events.pipe(filter(event => event instanceof NavigationEnd)).subscribe(() => { void this.refreshOpenStories(); });
    this.openStoryWorkspace.changed.subscribe(() => { void this.refreshOpenStories(); });
  }

  async refreshOpenStories(): Promise<void> { this.openStories = await this.openStoryWorkspace.list(); }

  beginRename(story: StoryState): void { this.renamingId = story.id; this.renameValue = story.label; }

  async saveRename(story: StoryState): Promise<void> {
    const label = this.renameValue.trim();
    if (!label) return;
    story.label = label;
    await this.db.saveStory(story);
    this.renamingId = '';
    this.openStoryWorkspace.notifyChanged();
  }

  cancelRename(): void { this.renamingId = ''; }

  async close(storyId: string): Promise<void> {
    await this.openStoryWorkspace.close(storyId);
    this.openStories = this.openStories.filter(story => story.id !== storyId);
    if (this.router.url === `/stories/${storyId}`) await this.router.navigate(['/stories']);
  }
}
