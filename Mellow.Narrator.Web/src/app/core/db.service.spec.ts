import { DbService } from './db.service';
import { StoryDefinition } from './models';

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
