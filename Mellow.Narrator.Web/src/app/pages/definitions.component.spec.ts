import { TestBed } from '@angular/core/testing';
import { MatSnackBar } from '@angular/material/snack-bar';
import { provideRouter } from '@angular/router';
import { vi } from 'vitest';
import { DbService } from '../core/db.service';
import { defaultSettings } from '../core/defaults';
import { StoryDefinition } from '../core/models';
import { NarratorService } from '../core/narrator.service';
import { DefinitionsComponent } from './definitions.component';

function createDatabaseStub(persisted: StoryDefinition[] = []) {
  return {
    definitions: vi.fn(async () => [...persisted]),
    definition: vi.fn(async () => undefined),
    settings: vi.fn(async () => defaultSettings()),
    saveDefinition: vi.fn(async (definition: StoryDefinition) => {
      persisted.push(definition);
    }),
  };
}

describe('DefinitionsComponent', () => {
  it('adds an imported definition to the rendered list', async () => {
    const persisted: StoryDefinition[] = [];
    const database = createDatabaseStub(persisted);

    await TestBed.configureTestingModule({
      imports: [DefinitionsComponent],
      providers: [
        provideRouter([]),
        { provide: DbService, useValue: database },
        { provide: NarratorService, useValue: {} },
        { provide: MatSnackBar, useValue: { open: vi.fn() } },
      ],
    }).compileComponents();

    const fixture = TestBed.createComponent(DefinitionsComponent);
    fixture.detectChanges();
    await fixture.whenStable();

    const file = {
      text: async () => JSON.stringify({
        formatVersion: 1,
        definition: {
          id: 'imported-id',
          title: 'Imported World',
          storyPrompt: 'A city that dreams.',
          initialEventsPrompt: 'The bells ring at midnight.',
          initialStoryBible: { entries: [] },
        },
      }),
    } as File;
    const input = document.createElement('input');
    Object.defineProperty(input, 'files', { value: [file] });

    await fixture.componentInstance.importFile({ target: input } as unknown as Event);
    fixture.detectChanges();

    expect(database.saveDefinition).toHaveBeenCalledOnce();
    expect(fixture.componentInstance.definitions).toHaveLength(1);
    expect((fixture.nativeElement as HTMLElement).textContent).toContain('Imported World');
  });

  it('rejects an import missing its story prompt instead of saving malformed data', async () => {
    const persisted: StoryDefinition[] = [];
    const database = createDatabaseStub(persisted);
    const snack = { open: vi.fn() };

    await TestBed.configureTestingModule({
      imports: [DefinitionsComponent],
      providers: [
        provideRouter([]),
        { provide: DbService, useValue: database },
        { provide: NarratorService, useValue: {} },
        { provide: MatSnackBar, useValue: snack },
      ],
    }).compileComponents();

    const fixture = TestBed.createComponent(DefinitionsComponent);
    fixture.detectChanges();
    await fixture.whenStable();

    const file = {
      text: async () => JSON.stringify({
        formatVersion: 1,
        definition: { id: 'imported-id', title: 'Imported World' },
      }),
    } as File;
    const input = document.createElement('input');
    Object.defineProperty(input, 'files', { value: [file] });

    await fixture.componentInstance.importFile({ target: input } as unknown as Event);

    expect(database.saveDefinition).not.toHaveBeenCalled();
    expect(fixture.componentInstance.definitions).toHaveLength(0);
    expect(snack.open).toHaveBeenCalledWith(expect.stringMatching(/Story Prompt/), 'Dismiss', expect.anything());
  });

  it('regenerates a definition by generating a fresh one and retiring the original to Trash', async () => {
    const original: StoryDefinition = {
      id: 'original-id',
      title: 'The Lighthouse',
      storyPrompt: 'A keeper guards a light that must never go out.',
      initialEventsPrompt: '',
      initialStoryBible: [],
      initialPlannedEvents: [],
      initialVictoryConditions: [],
      initialLossConditions: [],
      sortOrder: 3,
      createdAtUtc: '2026-01-01T00:00:00.000Z',
      updatedAtUtc: '2026-01-01T00:00:00.000Z',
    };
    const persisted: StoryDefinition[] = [original];
    const database = createDatabaseStub(persisted);
    const regeneratedDefinition: StoryDefinition = { ...original, id: 'regenerated-id', sortOrder: 99 };
    const narrator = {
      generateDefinition: vi.fn(async () => ({ ...regeneratedDefinition })),
      trashDefinition: vi.fn(async () => {}),
    };

    await TestBed.configureTestingModule({
      imports: [DefinitionsComponent],
      providers: [
        provideRouter([]),
        { provide: DbService, useValue: database },
        { provide: NarratorService, useValue: narrator },
        { provide: MatSnackBar, useValue: { open: vi.fn() } },
      ],
    }).compileComponents();

    const fixture = TestBed.createComponent(DefinitionsComponent);
    fixture.detectChanges();
    await fixture.whenStable();

    await fixture.componentInstance.regenerate(original);

    expect(narrator.generateDefinition).toHaveBeenCalledWith(original.title, original.storyPrompt);
    expect(database.saveDefinition).toHaveBeenCalledWith(expect.objectContaining({ id: 'regenerated-id', sortOrder: original.sortOrder }));
    expect(narrator.trashDefinition).toHaveBeenCalledWith(original);
  });
});
