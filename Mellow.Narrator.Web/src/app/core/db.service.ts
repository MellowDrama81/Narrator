import { Injectable } from '@angular/core';
import { defaultSettings } from './defaults';
import { AppSettings, StoryDefinition, StoryState, TrashItem } from './models';

type StoreName = 'settings' | 'definitions' | 'stories' | 'trash' | 'meta';

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
      request.onsuccess = () => resolve(request.result);
      request.onerror = () => reject(request.error);
      transaction.onabort = () => reject(transaction.error);
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
  definitions(): Promise<StoryDefinition[]> { return this.getAll<StoryDefinition>('definitions'); }
  definition(id: string): Promise<StoryDefinition | undefined> { return this.get('definitions', id); }
  saveDefinition(value: StoryDefinition): Promise<void> { return this.put('definitions', value); }
  deleteDefinition(id: string): Promise<void> { return this.remove('definitions', id); }
  stories(): Promise<StoryState[]> { return this.getAll<StoryState>('stories'); }
  story(id: string): Promise<StoryState | undefined> { return this.get('stories', id); }
  saveStory(value: StoryState): Promise<void> { return this.put('stories', value); }
  deleteStory(id: string): Promise<void> { return this.remove('stories', id); }
  trash(): Promise<TrashItem[]> { return this.getAll<TrashItem>('trash'); }
  saveTrash(value: TrashItem): Promise<void> { return this.put('trash', value); }
  deleteTrash(id: string): Promise<void> { return this.remove('trash', id); }
  meta<T>(key: string): Promise<T | undefined> { return this.get<{ key: string; value: T }>('meta', key).then(x => x?.value); }
  saveMeta<T>(key: string, value: T): Promise<void> { return this.put('meta', { key, value }); }
}

