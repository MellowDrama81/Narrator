import { TestBed } from '@angular/core/testing';
import { MatSnackBar } from '@angular/material/snack-bar';
import { provideRouter } from '@angular/router';
import { vi } from 'vitest';
import { defaultSettings } from '../core/defaults';
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
    expect(element.querySelectorAll('input')).toHaveLength(42);
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

  it('rejects invalid settings on save without persisting them', async () => {
    const saveSettings = vi.fn().mockResolvedValue(undefined);
    const open = vi.fn();
    await TestBed.configureTestingModule({
      imports: [SettingsComponent],
      providers: [
        provideRouter([]),
        { provide: DbService, useValue: { settings: () => Promise.resolve(defaultSettings()), saveSettings } },
        { provide: LlmService, useValue: {} },
        { provide: MatSnackBar, useValue: { open } },
      ],
    }).compileComponents();

    const fixture = TestBed.createComponent(SettingsComponent);
    fixture.detectChanges();
    await fixture.whenStable();

    fixture.componentInstance.settings.minSuggestedActions = 5;
    fixture.componentInstance.settings.maxSuggestedActions = 3;
    await fixture.componentInstance.save();

    expect(saveSettings).not.toHaveBeenCalled();
    expect(open).toHaveBeenCalledWith(expect.stringContaining('minSuggestedActions'), 'Dismiss', expect.anything());
  });

  it('rejects a blank (NaN) numeric field on save without persisting it', async () => {
    const saveSettings = vi.fn().mockResolvedValue(undefined);
    const open = vi.fn();
    await TestBed.configureTestingModule({
      imports: [SettingsComponent],
      providers: [
        provideRouter([]),
        { provide: DbService, useValue: { settings: () => Promise.resolve(defaultSettings()), saveSettings } },
        { provide: LlmService, useValue: {} },
        { provide: MatSnackBar, useValue: { open } },
      ],
    }).compileComponents();

    const fixture = TestBed.createComponent(SettingsComponent);
    fixture.detectChanges();
    await fixture.whenStable();

    fixture.componentInstance.settings.maxOutputTokens = NaN;
    await fixture.componentInstance.save();

    expect(saveSettings).not.toHaveBeenCalled();
    expect(open).toHaveBeenCalledWith(expect.stringContaining('maxOutputTokens'), 'Dismiss', expect.anything());
  });

  it('saves valid settings', async () => {
    const saveSettings = vi.fn().mockResolvedValue(undefined);
    const open = vi.fn();
    await TestBed.configureTestingModule({
      imports: [SettingsComponent],
      providers: [
        provideRouter([]),
        { provide: DbService, useValue: { settings: () => Promise.resolve(defaultSettings()), saveSettings } },
        { provide: LlmService, useValue: {} },
        { provide: MatSnackBar, useValue: { open } },
      ],
    }).compileComponents();

    const fixture = TestBed.createComponent(SettingsComponent);
    fixture.detectChanges();
    await fixture.whenStable();

    await fixture.componentInstance.save();

    expect(saveSettings).toHaveBeenCalledTimes(1);
    expect(open).toHaveBeenCalledWith('Settings saved to this browser.', 'Dismiss', expect.anything());
  });

  it('resets negotiated connection capabilities when the base URL changes', async () => {
    const saveSettings = vi.fn().mockResolvedValue(undefined);
    await TestBed.configureTestingModule({
      imports: [SettingsComponent],
      providers: [
        provideRouter([]),
        {
          provide: DbService,
          useValue: {
            settings: () => Promise.resolve({ ...defaultSettings(), structuredOutputTier: 'strictJsonSchema' as const }),
            saveSettings,
          },
        },
        { provide: LlmService, useValue: {} },
        { provide: MatSnackBar, useValue: { open: vi.fn() } },
      ],
    }).compileComponents();

    const fixture = TestBed.createComponent(SettingsComponent);
    fixture.detectChanges();
    await fixture.whenStable();

    fixture.componentInstance.settings.baseUrl = 'https://different.example/v1';
    await fixture.componentInstance.save();

    expect(saveSettings).toHaveBeenCalledTimes(1);
    expect(fixture.componentInstance.settings.structuredOutputTier).toBe('untested');
  });
});
