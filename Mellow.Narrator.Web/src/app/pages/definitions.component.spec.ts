import { TestBed } from '@angular/core/testing';
import { MatSnackBar } from '@angular/material/snack-bar';
import { provideRouter } from '@angular/router';
import { vi } from 'vitest';
import { DbService } from '../core/db.service';
import { StoryDefinition } from '../core/models';
import { NarratorService } from '../core/narrator.service';
import { DefinitionsComponent } from './definitions.component';

describe('DefinitionsComponent', () => {
  it('adds an imported definition to the rendered list', async () => {
    const persisted: StoryDefinition[] = [];
    const database = {
      definitions: vi.fn(async () => [...persisted]),
      definition: vi.fn(async () => undefined),
      saveDefinition: vi.fn(async (definition: StoryDefinition) => {
        persisted.push(definition);
      }),
    };

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
});
