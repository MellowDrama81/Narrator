import { TestBed } from '@angular/core/testing';
import { MatSnackBar } from '@angular/material/snack-bar';
import { provideRouter } from '@angular/router';
import { vi } from 'vitest';
import { DbService } from '../core/db.service';
import { LlmService } from '../core/llm.service';
import { SettingsComponent } from './settings.component';

describe('SettingsComponent', () => {
  it('renders the LLM controls before IndexedDB finishes opening', async () => {
    await TestBed.configureTestingModule({
      imports: [SettingsComponent],
      providers: [
        provideRouter([]),
        { provide: DbService, useValue: { settings: () => new Promise(() => undefined) } },
        { provide: LlmService, useValue: {} },
        { provide: MatSnackBar, useValue: { open: vi.fn() } },
      ],
    }).compileComponents();

    const fixture = TestBed.createComponent(SettingsComponent);
    fixture.detectChanges();
    const element = fixture.nativeElement as HTMLElement;

    expect(element.textContent).toContain('OpenAI-compatible API');
    expect(element.querySelectorAll('input')).toHaveLength(16);
    expect(element.textContent).toContain('Save settings');
  });

  it('keeps the controls visible when IndexedDB cannot be opened', async () => {
    await TestBed.configureTestingModule({
      imports: [SettingsComponent],
      providers: [
        provideRouter([]),
        { provide: DbService, useValue: { settings: () => Promise.reject(new Error('blocked')) } },
        { provide: LlmService, useValue: {} },
        { provide: MatSnackBar, useValue: { open: vi.fn() } },
      ],
    }).compileComponents();

    const fixture = TestBed.createComponent(SettingsComponent);
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
    const element = fixture.nativeElement as HTMLElement;

    expect(element.textContent).toContain('Browser storage could not be opened');
    expect(element.textContent).toContain('OpenAI-compatible API');
  });

  it('shows a visible model selector after models load', async () => {
    await TestBed.configureTestingModule({
      imports: [SettingsComponent],
      providers: [
        provideRouter([]),
        { provide: DbService, useValue: { settings: () => new Promise(() => undefined) } },
        { provide: LlmService, useValue: {} },
        { provide: MatSnackBar, useValue: { open: vi.fn() } },
      ],
    }).compileComponents();

    const fixture = TestBed.createComponent(SettingsComponent);
    fixture.componentInstance.models = ['gpt-4.1', 'gpt-5'];
    fixture.detectChanges();
    const element = fixture.nativeElement as HTMLElement;

    expect(element.querySelector('mat-select')).toBeTruthy();
    expect(element.textContent).toContain('2 models available');
  });
});
