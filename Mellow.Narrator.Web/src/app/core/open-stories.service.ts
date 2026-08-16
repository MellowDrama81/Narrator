import { Injectable } from '@angular/core';
import { Subject } from 'rxjs';
import { DbService } from './db.service';
import { StoryState } from './models';

const OPEN_STORY_IDS_KEY = 'open-story-ids';

@Injectable({ providedIn: 'root' })
export class OpenStoriesService {
  readonly changed = new Subject<void>();
  constructor(private readonly db: DbService) {}

  async list(): Promise<StoryState[]> {
    const ids = await this.ids();
    const values = await Promise.all(ids.map(id => this.db.story(id)));
    const stories = values.filter((value): value is StoryState => !!value);
    if (stories.length !== ids.length) await this.saveIds(stories.map(story => story.id));
    return stories;
  }

  async open(storyId: string): Promise<void> {
    const ids = await this.ids();
    if (!ids.includes(storyId)) { await this.saveIds([...ids, storyId]); this.changed.next(); }
  }

  async close(storyId: string): Promise<void> {
    await this.saveIds((await this.ids()).filter(id => id !== storyId));
    this.changed.next();
  }

  notifyChanged(): void { this.changed.next(); }

  private async ids(): Promise<string[]> {
    return (await this.db.meta<string[]>(OPEN_STORY_IDS_KEY) ?? []).filter(id => typeof id === 'string' && id.trim());
  }

  private saveIds(ids: string[]): Promise<void> { return this.db.saveMeta(OPEN_STORY_IDS_KEY, ids); }
}
