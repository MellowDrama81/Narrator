import { DbService, TRASH_MAX_ITEMS, TRASH_MAX_SIZE_BYTES } from './db.service';
import { StoryDefinition, TrashItem } from './models';

// A minimal in-memory stand-in for IndexedDB, just enough to exercise DbService's real get/getAll/put/
// delete plumbing (including the transaction.oncomplete-driven resolution) without a browser. Each
// object store method schedules its result on a microtask, mirroring how a real IDBRequest resolves
// after the calling code has finished wiring up onsuccess/onerror/oncomplete handlers.
function createFakeDatabase(storeKeyPaths: Record<string, string>): IDBDatabase {
  const stores = new Map<string, Map<unknown, unknown>>(Object.keys(storeKeyPaths).map(name => [name, new Map()]));

  return {
    transaction: (storeName: string | string[]) => {
      const name = Array.isArray(storeName) ? storeName[0] : storeName;
      const data = stores.get(name)!;
      const keyPath = storeKeyPaths[name];
      const transaction: any = { error: null, onabort: null, oncomplete: null, onerror: null };
      // Schedules `compute` on a microtask so it runs after DbService.request has synchronously wired
      // up request.onsuccess/transaction.oncomplete, matching real IndexedDB's async resolution.
      const schedule = (request: any, compute: () => unknown) => {
        queueMicrotask(() => {
          request.result = compute();
          request.onsuccess?.(new Event('success'));
          queueMicrotask(() => transaction.oncomplete?.(new Event('complete')));
        });
      };
      transaction.objectStore = () => ({
        get: (key: IDBValidKey) => { const request: any = {}; schedule(request, () => data.get(key)); return request; },
        getAll: () => { const request: any = {}; schedule(request, () => Array.from(data.values())); return request; },
        put: (value: any) => {
          const request: any = {};
          schedule(request, () => { data.set(value[keyPath], value); return value[keyPath]; });
          return request;
        },
        delete: (key: IDBValidKey) => { const request: any = {}; schedule(request, () => { data.delete(key); return undefined; }); return request; },
      });
      return transaction as IDBTransaction;
    },
  } as unknown as IDBDatabase;
}

function createDbService(storeKeyPaths: Record<string, string> = { trash: 'trashId', definitions: 'id', stories: 'id', settings: 'key', meta: 'key' }): DbService {
  const service = Object.create(DbService.prototype) as DbService;
  Object.defineProperty(service, 'database', { value: Promise.resolve(createFakeDatabase(storeKeyPaths)) });
  return service;
}

const trashItem = (overrides: Partial<TrashItem> = {}): TrashItem => ({
  trashId: `trash-${Math.random()}`,
  type: 'definition',
  originalId: 'original-id',
  displayName: 'A trashed item',
  deletedAtUtc: '2026-01-01T00:00:00.000Z',
  payload: { title: 'x' } as unknown as TrashItem['payload'],
  ...overrides,
});

describe('DbService', () => {
  it('does not report a definition saved until its IndexedDB transaction commits', async () => {
    const request = {} as IDBRequest<IDBValidKey>;
    Object.defineProperty(request, 'result', { get: () => 'definition-id' });
    const store = { put: vi.fn(() => request) } as unknown as IDBObjectStore;
    const transaction = {
      error: null,
      objectStore: vi.fn(() => store),
      onabort: null,
      oncomplete: null,
      onerror: null,
    } as unknown as IDBTransaction;
    const database = {
      transaction: vi.fn(() => transaction),
    } as unknown as IDBDatabase;
    const service = Object.create(DbService.prototype) as DbService;
    Object.defineProperty(service, 'database', { value: Promise.resolve(database) });
    const definition: StoryDefinition = {
      id: 'definition-id',
      title: 'Persistent story',
      storyPrompt: 'A story that survives reloads.',
      initialEventsPrompt: '',
      initialStoryBible: [],
      initialPlannedEvents: [],
      initialVictoryConditions: [],
      initialLossConditions: [],
      sortOrder: 0,
      createdAtUtc: '2026-01-01T00:00:00.000Z',
      updatedAtUtc: '2026-01-01T00:00:00.000Z',
    };

    let settled = false;
    const save = service.saveDefinition(definition).then(() => {
      settled = true;
    });
    await Promise.resolve();
    await Promise.resolve();

    request.onsuccess?.(new Event('success'));
    await Promise.resolve();
    expect(settled).toBe(false);

    transaction.oncomplete?.(new Event('complete'));
    await save;
    expect(settled).toBe(true);
  });
});

// Mirrors JsonNarratorStore.PurgeTrashAsync: after every move-to-trash, the oldest trash items are
// purged while there are more than TRASH_MAX_ITEMS or the total payload size exceeds
// TRASH_MAX_SIZE_BYTES, but at least one item is always left even if it alone exceeds the size cap.
describe('DbService trash auto-purge', () => {
  it('does not purge while at or under both caps', async () => {
    const service = createDbService();
    for (let i = 0; i < TRASH_MAX_ITEMS; i++) {
      await service.saveTrash(trashItem({ trashId: `trash-${i}`, deletedAtUtc: `2026-01-${String(i + 1).padStart(2, '0')}T00:00:00.000Z` }));
    }
    const remaining = await service.trash();
    expect(remaining).toHaveLength(TRASH_MAX_ITEMS);
  });

  it('purges the oldest items beyond the item-count cap, oldest-first', async () => {
    const service = createDbService();
    const items = Array.from({ length: TRASH_MAX_ITEMS + 2 }, (_, i) => trashItem({
      trashId: `trash-${i}`,
      deletedAtUtc: `2026-01-${String(i + 1).padStart(2, '0')}T00:00:00.000Z`,
    }));
    for (const item of items) {
      await service.saveTrash(item);
    }
    const remaining = await service.trash();
    expect(remaining).toHaveLength(TRASH_MAX_ITEMS);
    const remainingIds = new Set(remaining.map(x => x.trashId));
    // The two oldest (trash-0, trash-1) should have been purged; everything newer survives.
    expect(remainingIds.has('trash-0')).toBe(false);
    expect(remainingIds.has('trash-1')).toBe(false);
    expect(remainingIds.has(`trash-${TRASH_MAX_ITEMS + 1}`)).toBe(true);
  });

  it('purges the oldest item when the total payload size exceeds the cap even under the item-count cap', async () => {
    const service = createDbService();
    const big = 'x'.repeat(Math.ceil(TRASH_MAX_SIZE_BYTES * 0.6));
    const older = trashItem({ trashId: 'older', deletedAtUtc: '2026-01-01T00:00:00.000Z', payload: { title: big } as any });
    const newer = trashItem({ trashId: 'newer', deletedAtUtc: '2026-01-02T00:00:00.000Z', payload: { title: big } as any });
    await service.saveTrash(older);
    await service.saveTrash(newer);
    const remaining = await service.trash();
    // Two items at ~60% of the cap each exceed the cap combined (well under the 10-item cap), so the
    // older one should have been purged, leaving only the newer one.
    expect(remaining).toHaveLength(1);
    expect(remaining[0].trashId).toBe('newer');
  });

  it('always leaves at least one item even if it alone exceeds the size cap', async () => {
    const service = createDbService();
    const huge = 'x'.repeat(TRASH_MAX_SIZE_BYTES + 1024);
    const small = trashItem({ trashId: 'small', deletedAtUtc: '2026-01-01T00:00:00.000Z' });
    const lone = trashItem({ trashId: 'lone', deletedAtUtc: '2026-01-02T00:00:00.000Z', payload: { title: huge } as any });
    await service.saveTrash(small);
    await service.saveTrash(lone);
    const remaining = await service.trash();
    expect(remaining).toHaveLength(1);
    expect(remaining[0].trashId).toBe('lone');
  });
});
