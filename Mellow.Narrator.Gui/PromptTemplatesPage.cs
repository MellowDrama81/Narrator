using Mellow.Narrator.Core;

namespace Mellow.Narrator.Gui;

public sealed class PromptTemplatesPage : ContentPage
{
    private readonly INarratorApplication _app;
    private readonly Editor _storyDefinition = TemplateEditor();
    private readonly Editor _storyNarration = TemplateEditor(180);
    private readonly Editor _correctiveRetry = TemplateEditor();
    private readonly Editor _promptedJson = TemplateEditor();
    private readonly Editor _openingScene = TemplateEditor();
    private readonly Editor _continueStory = TemplateEditor();
    private ApiConnectionSettings? _settings;

    public PromptTemplatesPage(INarratorApplication app)
    {
        _app = app;
        Title = "Prompt Templates";
        var content = new VerticalStackLayout { Padding = 16, Spacing = 8 };
        content.Children.Add(Ui.Heading("Prompt Templates"));
        content.Children.Add(new Label
        {
            Text = "These instructions apply to every subsequent LLM request. Story Prompts remain part of each Story Definition.",
            FontSize = 12
        });
        Add(content, "Initial Story Bible instruction", _storyDefinition);
        Add(content, "Story narration instruction", _storyNarration);
        Add(
            content,
            $"Corrective retry instruction — must contain {PromptTemplateDefaults.ValidationErrorPlaceholder}",
            _correctiveRetry);
        Add(
            content,
            $"Prompted-JSON instruction — must contain {PromptTemplateDefaults.SchemaPlaceholder}",
            _promptedJson);
        Add(content, "Opening-scene request", _openingScene);
        Add(content, "Continue-story fallback request", _continueStory);
        content.Children.Add(new Label
        {
            Text = $"Each template is limited to {PromptTemplateDefaults.MaximumTemplateCharacters:N0} characters. Reset changes the fields but does not persist until Save.",
            FontSize = 12
        });
        content.Children.Add(Ui.Buttons(
            Ui.Button("Save", Save),
            Ui.Button("Reset Prompt Templates", Reset)));
        Content = new ScrollView { Content = content };
        ToolbarItems.Add(new ToolbarItem("Done", null, async () => await Navigation.PopModalAsync()));
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (_settings is not null) return;
        try
        {
            _settings = await _app.GetSettingsAsync();
            Load(_settings.PromptTemplates);
        }
        catch (Exception ex)
        {
            await Ui.Error(this, ex);
        }
    }

    private async void Save(object? sender, EventArgs e)
    {
        try
        {
            var current = _settings ?? await _app.GetSettingsAsync();
            var updated = current with { PromptTemplates = Build() };
            await _app.SaveSettingsAsync(updated, null);
            _settings = updated;
            await DisplayAlertAsync("Prompt Templates", "Prompt templates saved.", "OK");
        }
        catch (Exception ex)
        {
            await Ui.Error(this, ex);
        }
    }

    private void Reset(object? sender, EventArgs e) => Load(PromptTemplateDefaults.Create());

    private PromptTemplateSettings Build() => new(
        _storyDefinition.Text ?? "",
        _storyNarration.Text ?? "",
        _correctiveRetry.Text ?? "",
        _promptedJson.Text ?? "",
        _openingScene.Text ?? "",
        _continueStory.Text ?? "");

    private void Load(PromptTemplateSettings templates)
    {
        _storyDefinition.Text = templates.StoryDefinitionInstruction;
        _storyNarration.Text = templates.StoryNarrationInstruction;
        _correctiveRetry.Text = templates.CorrectiveRetryInstruction;
        _promptedJson.Text = templates.PromptedJsonInstruction;
        _openingScene.Text = templates.OpeningSceneInstruction;
        _continueStory.Text = templates.ContinueStoryInstruction;
    }

    private static void Add(Layout content, string label, Editor editor)
    {
        content.Children.Add(new Label { Text = label });
        content.Children.Add(editor);
    }

    private static Editor TemplateEditor(double minimumHeight = 110) => new()
    {
        AutoSize = EditorAutoSizeOption.TextChanges,
        MinimumHeightRequest = minimumHeight,
        MaxLength = PromptTemplateDefaults.MaximumTemplateCharacters
    };
}
