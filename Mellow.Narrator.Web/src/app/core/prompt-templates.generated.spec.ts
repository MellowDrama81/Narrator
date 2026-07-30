import { promptTemplates } from './prompt-templates.generated';

describe('generated prompt templates', () => {
  it('contains every shared prompt', () => {
    expect(Object.keys(promptTemplates)).toEqual([
      'storyDefinitionInstruction',
      'storyNarrationInstruction',
      'correctiveRetryInstruction',
      'promptedJsonInstruction',
      'openingSceneInstruction',
      'continueStoryInstruction',
    ]);
  });

  it('preserves declared placeholders for runtime substitution', () => {
    expect(promptTemplates.storyNarrationInstruction).toContain('{minParagraphs}');
    expect(promptTemplates.storyNarrationInstruction).toContain('{maxSuggestedActions}');
    expect(promptTemplates.correctiveRetryInstruction).toContain('{validationError}');
    expect(promptTemplates.promptedJsonInstruction).toContain('{schema}');
  });
});
