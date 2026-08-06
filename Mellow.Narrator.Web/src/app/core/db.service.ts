import { Injectable } from '@angular/core';
import { defaultSettings } from './defaults';
import { AppSettings, StoryDefinition, StoryState, TrashItem } from './models';

type StoreName = 'settings' | 'definitions' | 'stories' | 'trash' | 'meta';

// initialPlannedEvents/currentPlannedEvents/relevantPlannedEventIds/plannedEventUpdates were added to
// these types after they first shipped, so a record written to IndexedDB before then has no such
// property at all - not even `undefined` as a key, since structured-clone-based storage preserves
// exactly the shape an object had when it was put. TypeScript's static types claim these fields are
// always present, but nothing enforces that for data already sitting in a user's browser, so any code
// (applyPlannedEvents, the Planned Events editor, ...) that dereferences them unconditionally would
// throw. Backfilling once here, at the single boundary where stored data re-enters the app, avoids
// that - the same fix applied to the .NET app's persistence layer for the identical failure mode.
function normalizeDefinition(value: StoryDefinition): StoryDefinition {
  return {
    ...value,
    initialPlannedEvents: value.initialPlannedEvents ?? [],
    initialVictoryConditions: value.initialVictoryConditions ?? [],
    initialLossConditions: value.initialLossConditions ?? [],
  };
}

function normalizeStory(value: StoryState): StoryState {
  return {
    ...value,
    definition: {
      ...value.definition,
      initialPlannedEvents: value.definition.initialPlannedEvents ?? [],
      initialVictoryConditions: value.definition.initialVictoryConditions ?? [],
      initialLossConditions: value.definition.initialLossConditions ?? [],
    },
    currentPlannedEvents: value.currentPlannedEvents ?? [],
    currentVictoryConditions: value.currentVictoryConditions ?? [],
    currentLossConditions: value.currentLossConditions ?? [],
    revealedVictoryConditionIds: value.revealedVictoryConditionIds ?? [],
    metVictoryConditionIds: value.metVictoryConditionIds ?? [],
    revealedLossConditionIds: value.revealedLossConditionIds ?? [],
    metLossConditionIds: value.metLossConditionIds ?? [],
    turns: value.turns.map(turn => ({
      ...turn,
      relevantPlannedEventIds: turn.relevantPlannedEventIds ?? [],
      plannedEventUpdates: turn.plannedEventUpdates ?? [],
      revealedVictoryConditionIds: turn.revealedVictoryConditionIds ?? [],
      metVictoryConditionIds: turn.metVictoryConditionIds ?? [],
      revealedLossConditionIds: turn.revealedLossConditionIds ?? [],
      metLossConditionIds: turn.metLossConditionIds ?? [],
    })),
  };
}

@Injectable({ providedIn: 'root' })
export class DbService {
  private readonly database = this.open();

  private open(): Promise<IDBDatabase> {
    return new Promise((resolve, reject) => {
      const request = indexedDB.open('mellow-narrator', 1);
      request.onupgradeneeded = () => {
        const db = request.result;
        if (!db.objectStoreNames.contains('settings')) db.createObjectStore('settings', { keyPath: 'key' });
        if (!db.objectStoreNames.contains('definitions')) db.createObjectStore('definitions', { keyPath: 'id' });
        if (!db.objectStoreNames.contains('stories')) db.createObjectStore('stories', { keyPath: 'id' });
        if (!db.objectStoreNames.contains('trash')) db.createObjectStore('trash', { keyPath: 'trashId' });
        if (!db.objectStoreNames.contains('meta')) db.createObjectStore('meta', { keyPath: 'key' });
      };
      request.onsuccess = () => resolve(request.result);
      request.onerror = () => reject(request.error);
      request.onblocked = () => reject(new Error('Mellow Narrator storage is open in another browser context.'));
    });
  }

  private async request<T>(storeName: StoreName, mode: IDBTransactionMode, action: (store: IDBObjectStore) => IDBRequest<T>): Promise<T> {
    const db = await this.database;
    return new Promise<T>((resolve, reject) => {
      const transaction = db.transaction(storeName, mode);
      const request = action(transaction.objectStore(storeName));
      let requestSucceeded = false;
      let result!: T;

      request.onsuccess = () => {
        requestSucceeded = true;
        result = request.result;
      };
      request.onerror = () => reject(request.error ?? new Error(`IndexedDB request failed for ${storeName}.`));
      transaction.oncomplete = () => {
        if (requestSucceeded) resolve(result);
        else reject(new Error(`IndexedDB transaction completed before its ${storeName} request succeeded.`));
      };
      transaction.onerror = () => reject(transaction.error ?? new Error(`IndexedDB transaction failed for ${storeName}.`));
      transaction.onabort = () => reject(transaction.error ?? new Error(`IndexedDB transaction was aborted for ${storeName}.`));
    });
  }

  private get<T>(store: StoreName, key: IDBValidKey): Promise<T | undefined> {
    return this.request<T | undefined>(store, 'readonly', value => value.get(key));
  }

  private getAll<T>(store: StoreName): Promise<T[]> {
    return this.request<T[]>(store, 'readonly', value => value.getAll());
  }

  private async put<T>(store: StoreName, value: T): Promise<void> {
    await this.request<IDBValidKey>(store, 'readwrite', target => target.put(value));
  }

  private async remove(store: StoreName, key: IDBValidKey): Promise<void> {
    await this.request<undefined>(store, 'readwrite', target => target.delete(key));
  }

  async settings(): Promise<AppSettings> {
    return { ...defaultSettings(), ...(await this.get<AppSettings>('settings', 'app')) };
  }

  saveSettings(settings: AppSettings): Promise<void> { return this.put('settings', settings); }

  async definitions(): Promise<StoryDefinition[]> {
    return (await this.getAll<StoryDefinition>('definitions')).map(normalizeDefinition);
  }

  async definition(id: string): Promise<StoryDefinition | undefined> {
    const value = await this.get<StoryDefinition>('definitions', id);
    return value && normalizeDefinition(value);
  }

  saveDefinition(value: StoryDefinition): Promise<void> { return this.put('definitions', value); }
  deleteDefinition(id: string): Promise<void> { return this.remove('definitions', id); }

  async stories(): Promise<StoryState[]> {
    return (await this.getAll<StoryState>('stories')).map(normalizeStory);
  }

  async story(id: string): Promise<StoryState | undefined> {
    const value = await this.get<StoryState>('stories', id);
    return value && normalizeStory(value);
  }

  saveStory(value: StoryState): Promise<void> { return this.put('stories', value); }
  deleteStory(id: string): Promise<void> { return this.remove('stories', id); }
  trash(): Promise<TrashItem[]> { return this.getAll<TrashItem>('trash'); }
  saveTrash(value: TrashItem): Promise<void> { return this.put('trash', value); }
  deleteTrash(id: string): Promise<void> { return this.remove('trash', id); }
  meta<T>(key: string): Promise<T | undefined> { return this.get<{ key: string; value: T }>('meta', key).then(x => x?.value); }
  saveMeta<T>(key: string, value: T): Promise<void> { return this.put('meta', { key, value }); }
}
