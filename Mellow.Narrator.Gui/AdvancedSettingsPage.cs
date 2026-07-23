using System.Globalization;
using Mellow.Narrator.Core;

namespace Mellow.Narrator.Gui;

public sealed class AdvancedSettingsPage : ContentPage
{
    private readonly INarratorApplication _app;
    private readonly Dictionary<string, Entry> _fields = [];
    private ApiConnectionSettings? _settings;

    public AdvancedSettingsPage(INarratorApplication app)
    {
        _app = app;
        Title = "Advanced Settings";
        var content = new VerticalStackLayout { Padding = 16, Spacing = 6 };
        content.Children.Add(Ui.Heading("Advanced Settings"));
        Add(content, "Temperature (blank = provider default)", "temperature");
        Add(content, "Top-p (blank = provider default)", "topP");
        Add(content, "Reasoning effort (blank = provider default)", "reasoning");
        Add(content, "Bible entry character limit", "bibleEntry");
        Add(content, "Bible total character limit", "bibleTotal");
        Add(content, "Bible warning percent", "bibleWarning");
        Add(content, "Automatic retries", "retries");
        Add(content, "Initial retry delay seconds", "retryInitial");
        Add(content, "Maximum retry delay seconds", "retryMax");
        Add(content, "Maximum Retry-After seconds", "retryAfter");
        Add(content, "Title characters", "title");
        Add(content, "Label characters", "label");
        Add(content, "Story Prompt characters", "prompt");
        Add(content, "Question characters", "question");
        Add(content, "Validation instruction characters", "validation");
        Add(content, "Answer characters", "answer");
        Add(content, "Player action characters", "action");
        Add(content, "Narration characters", "narration");
        Add(content, "Suggested actions", "suggestedCount");
        Add(content, "Suggested action characters", "suggestedLength");
        Add(content, "Bible category characters", "category");
        Add(content, "Bible name characters", "name");
        Add(content, "Bible updates per response", "updates");
        Add(content, "HTTP response bytes", "responseBytes");
        content.Children.Add(Ui.Buttons(Ui.Button("Save", Save), Ui.Button("Reset defaults", Reset)));
        Content = new ScrollView { Content = content };
        ToolbarItems.Add(new ToolbarItem("Done", null, async () => await Navigation.PopModalAsync()));
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (_settings is null) { _settings = await _app.GetSettingsAsync(); Load(_settings); }
    }

    private async void Save(object? sender, EventArgs e)
    {
        try
        {
            var s = _settings ?? await _app.GetSettingsAsync();
            double? Optional(string key) => string.IsNullOrWhiteSpace(_fields[key].Text) ? null : Number(key);
            var updated = s with
            {
                Parameters = new(Optional("temperature"), Optional("topP"), string.IsNullOrWhiteSpace(_fields["reasoning"].Text) ? null : _fields["reasoning"].Text.Trim()),
                StoryGeneration = s.StoryGeneration with
                {
                    MaxStoryBibleEntryCharacters = Int("bibleEntry"),
                    MaxStoryBibleCharacters = Int("bibleTotal"),
                    StoryBibleWarningPercent = Int("bibleWarning")
                },
                Retry = new(Int("retries"), Seconds("retryInitial"), Seconds("retryMax"), Seconds("retryAfter")),
                ContentLimits = new(Int("title"), Int("label"), Int("prompt"), Int("question"), Int("validation"),
                    Int("answer"), Int("action"), Int("narration"), Int("suggestedCount"), Int("suggestedLength"),
                    Int("category"), Int("name"), Int("updates"), Int("responseBytes"))
            };
            await _app.SaveSettingsAsync(updated, null);
            _settings = updated;
            await DisplayAlertAsync("Advanced Settings", "Settings saved.", "OK");
        }
        catch (Exception ex) { await Ui.Error(this, ex); }
    }

    private void Reset(object? sender, EventArgs e)
    {
        var defaults = NarratorDefaults.Create();
        _settings = (_settings ?? defaults) with
        {
            Parameters = defaults.Parameters,
            StoryGeneration = (_settings ?? defaults).StoryGeneration with
            {
                MaxStoryBibleEntryCharacters = defaults.StoryGeneration.MaxStoryBibleEntryCharacters,
                MaxStoryBibleCharacters = defaults.StoryGeneration.MaxStoryBibleCharacters,
                StoryBibleWarningPercent = defaults.StoryGeneration.StoryBibleWarningPercent
            },
            Retry = defaults.Retry,
            ContentLimits = defaults.ContentLimits
        };
        Load(_settings);
    }

    private void Load(ApiConnectionSettings s)
    {
        Set("temperature", s.Parameters.Temperature);
        Set("topP", s.Parameters.TopP);
        _fields["reasoning"].Text = s.Parameters.ReasoningEffort ?? "";
        Set("bibleEntry", s.StoryGeneration.MaxStoryBibleEntryCharacters); Set("bibleTotal", s.StoryGeneration.MaxStoryBibleCharacters); Set("bibleWarning", s.StoryGeneration.StoryBibleWarningPercent);
        Set("retries", s.Retry.MaxAutomaticRetries); Set("retryInitial", s.Retry.InitialDelay.TotalSeconds); Set("retryMax", s.Retry.MaxDelay.TotalSeconds); Set("retryAfter", s.Retry.MaxRetryAfter.TotalSeconds);
        var c = s.ContentLimits;
        Set("title", c.MaxStoryTitleCharacters); Set("label", c.MaxStoryLabelCharacters); Set("prompt", c.MaxStoryPromptCharacters);
        Set("question", c.MaxPlayerQuestionCharacters); Set("validation", c.MaxValidationInstructionCharacters); Set("answer", c.MaxPlayerAnswerCharacters);
        Set("action", c.MaxPlayerActionCharacters); Set("narration", c.MaxNarrationCharacters); Set("suggestedCount", c.MaxSuggestedActions);
        Set("suggestedLength", c.MaxSuggestedActionCharacters); Set("category", c.MaxStoryBibleCategoryCharacters); Set("name", c.MaxStoryBibleNameCharacters);
        Set("updates", c.MaxStoryBibleUpdatesPerResponse); Set("responseBytes", c.MaxResponseBodyBytes);
    }

    private void Add(Layout content, string label, string key)
    {
        var entry = new Entry { Keyboard = key == "reasoning" ? Keyboard.Default : Keyboard.Numeric };
        _fields[key] = entry;
        content.Children.Add(new Label { Text = label });
        content.Children.Add(entry);
    }
    private void Set(string key, object? value) => _fields[key].Text = value is null ? "" : Convert.ToString(value, CultureInfo.InvariantCulture);
    private double Number(string key) => double.TryParse(_fields[key].Text, CultureInfo.InvariantCulture, out var value) ? value : throw new NarratorException($"Invalid value for {key}.");
    private int Int(string key) => checked((int)Number(key));
    private TimeSpan Seconds(string key) => TimeSpan.FromSeconds(Number(key));
}
