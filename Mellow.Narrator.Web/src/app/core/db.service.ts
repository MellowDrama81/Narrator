import { Injectable } from '@angular/core';
import { defaultSettings } from './defaults';
import { AppSettings, StoryDefinition, StoryState, TrashItem } from './models';

type StoreName = 'settings' | 'definitions' | 'stories' | 'trash' | 'meta';

// Mirrors JsonNarratorStore's PurgeTrashAsync cap of 10 items / 100MB total. Exported so tests can
// reference the exact cap instead of duplicating the literal.
export const TRASH_MAX_ITEMS = 10;
export const TRASH_MAX_SIZE_BYTES = 100 * 1024 * 1024;

// initialPlannedEvents/currentPlannedEvents/relevantPlannedEventIds/plannedEventUpdates/storySummary
// were added to these types after they first shipped, so a record written to IndexedDB before then has
// no such property at all - not even `undefined` as a key, since structured-clone-based storage preserves
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
    storySummary: value.storySummary ?? '',
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
    const settings = { ...defaultSettings(), ...(await this.get<AppSettings>('settings', 'app')) };
    // Migrate the original single browser connection into a named profile on first read.
    if (!settings.connections?.length) {
      const connection = { id: 'default', name: 'Default connection', baseUrl: settings.baseUrl, apiKey: settings.apiKey };
      settings.connections = [connection];
      settings.generationCallRoutes = Object.fromEntries([
        'storyDefinition', 'turn', 'adjudication', 'scenePlan', 'planCritic', 'narration',
        'storyBibleAnalysis', 'plannedEventAnalysis', 'conditionSummaryAnalysis', 'stateExtraction', 'proseRevision',
      ].map(call => [call, { connectionId: connection.id, modelId: settings.modelId }]));
    }
    return settings;
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

  async saveTrash(value: TrashItem): Promise<void> {
    await this.put('trash', value);
    await this.purgeTrash();
  }

  deleteTrash(id: string): Promise<void> { return this.remove('trash', id); }

  // Mirrors JsonNarratorStore.PurgeTrashAsync (Mellow.Narrator.Persistence): after every move-to-trash,
  // auto-purge the oldest trash items while there are more than 10 items or the total payload size
  // exceeds 100MB, but always leave at least 1 item even if that lone item alone exceeds the size cap.
  // The C# side measures on-disk file size; here JSON.stringify(payload).length is used as a reasonable
  // proxy for that, since there is no filesystem to stat.
  private async purgeTrash(): Promise<void> {
    let items = await this.trash();
    let totalSize = items.reduce((sum, item) => sum + DbService.trashItemSize(item), 0);
    const oldestFirst = [...items].sort((a, b) => a.deletedAtUtc.localeCompare(b.deletedAtUtc));
    const candidates = oldestFirst.slice(0, Math.max(0, items.length - 1));
    for (const item of candidates) {
      if (items.length <= TRASH_MAX_ITEMS && totalSize <= TRASH_MAX_SIZE_BYTES) break;
      await this.deleteTrash(item.trashId);
      totalSize -= DbService.trashItemSize(item);
      items = items.filter(x => x.trashId !== item.trashId);
    }
  }

  private static trashItemSize(item: TrashItem): number {
    return JSON.stringify(item.payload).length;
  }

  meta<T>(key: string): Promise<T | undefined> { return this.get<{ key: string; value: T }>('meta', key).then(x => x?.value); }
  saveMeta<T>(key: string, value: T): Promise<void> { return this.put('meta', { key, value }); }
}
