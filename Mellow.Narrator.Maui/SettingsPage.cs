using System.Globalization;
using Mellow.Narrator.Core;

namespace Mellow.Narrator.Maui;

public sealed class SettingsPage : ContentPage, IPendingOperationPage, IInFlightRequestPage
{
    private const string StoredCredentialIndicator = "stored-api-key";
    private static readonly IReadOnlyDictionary<string, string> Help = new Dictionary<string, string>
    {
        ["bibleEntry"] = "default 4000; range 100â€“50000",
        ["bibleTotal"] = "default 60000; range 1000â€“1000000",
        ["bibleWarning"] = "default 80%; range 50â€“95",
        ["retries"] = "default 2; range 0â€“5",
        ["retryInitial"] = "default 1; range 0.25â€“30",
        ["retryMax"] = "default 10; range 1â€“120",
        ["retryAfter"] = "default 60; range 1â€“600",
        ["title"] = "default 200; range 1â€“1000",
        ["label"] = "default 200; range 1â€“1000",
        ["prompt"] = "default 20000; range 100â€“200000",
        ["action"] = "default 4000; range 1â€“50000",
        ["narration"] = "default 20000; range 100â€“200000",
        ["suggestedMin"] = "default 2; range 1â€“20; must not exceed the maximum",
        ["suggestedCount"] = "default 3; range 1â€“20",
        ["suggestedLength"] = "default 500; range 1â€“5000",
        ["category"] = "default 100; range 1â€“1000",
        ["name"] = "default 200; range 1â€“2000",
        ["updates"] = "default 100; range 1â€“1000",
        ["responseBytes"] = "default 2097152; range 65536â€“16777216",
        ["plannedEventEntry"] = "default 2000; range 100â€“50000",
        ["plannedEventTotal"] = "default 20000; range 1000â€“1000000",
        ["plannedEventWarning"] = "default 80%; range 50â€“95",
        ["plannedEventDescription"] = "default 1000; range 1â€“5000",
        ["plannedEventCondition"] = "default 500; range 1â€“5000",
        ["plannedEventUpdates"] = "default 50; range 1â€“1000",
        ["conditionCount"] = "default 20; range 1â€“200",
        ["conditionDescription"] = "default 1000; range 1â€“5000",
        ["storySummary"] = "default 3000; range 500â€“20000",
        ["paragraphsMin"] = "default 4; range 1â€“20; must not exceed the maximum",
        ["paragraphsMax"] = "default 6; range 1â€“20",
        ["sentencesMin"] = "default 2; range 1â€“20; must not exceed the maximum",
        ["sentencesMax"] = "default 5; range 1â€“20"
    };
    private readonly INarratorApplication _app;
    private readonly ITrashStore _trash;
    private readonly MainTabbedPage _tabs;
    private readonly Entry _baseUrl = new() { Placeholder = "https://provider.example/v1" };
    private readonly Entry _model = new() { Placeholder = "Model ID" };
    private readonly Picker _discoveredModels = new() { Title = "Discovered models" };
    private readonly Entry _apiKey = new() { Placeholder = "API key (optional)", IsPassword = true };
    private readonly Entry _recentTurns = Numeric();
    private readonly Entry _maxEntries = Numeric();
    private readonly Entry _maxPlannedEvents = Numeric();
    private readonly Picker _turnPipeline = new()
    {
        Title = "Turn generation pipeline",
        ItemsSource = new[]
        {
            "1 call (standard)", "2 calls (draft + state)", "3 calls (adjudicate + draft + state)",
            "4 calls (adjudicate + plan + draft + state)", "5 calls (adds plan critic)",
            "7 calls (full sequential analysis)", "7 calls (parallel analysis)", "8 calls (full analysis + prose revision)"
        }
    };
    private readonly Picker _logLevel = new()
    {
        Title = "Logging level",
        ItemsSource = new[]
        {
            NarratorLogLevel.Off,
            NarratorLogLevel.Error,
            NarratorLogLevel.Warning,
            NarratorLogLevel.Information,
            NarratorLogLevel.Debug,
            NarratorLogLevel.Trace
        }
    };
    private readonly Dictionary<string, Entry> _fields = [];
    private readonly Label _status = new();
    private bool _hasStoredCredential;
    private bool _credentialEdited;
    private bool _updatingCredentialDisplay;
    private bool _clearCredential;
    private CancellationTokenSource? _request;
    private PendingOperationState? _pendingOperation;
    private bool _loaded;

    public SettingsPage(INarratorApplication app, ITrashStore trash, MainTabbedPage tabs)
    {
        _app = app;
        _trash = trash;
        _tabs = tabs;
        Title = "Settings";
        _discoveredModels.SelectedIndexChanged += (_, _) =>
        {
            if (_discoveredModels.SelectedItem is string model) _model.Text = model;
        };
        _apiKey.Focused += (_, _) =>
        {
            if (_hasStoredCredential && !_credentialEdited && !_clearCredential)
                SetCredentialText("");
        };
        _apiKey.Unfocused += (_, _) =>
        {
            if (_hasStoredCredential && !_credentialEdited && !_clearCredential)
                SetCredentialText(StoredCredentialIndicator);
        };
        _apiKey.TextChanged += (_, _) =>
        {
            if (_updatingCredentialDisplay) return;
            _credentialEdited = true;
            _clearCredential = false;
        };
        var clear = Ui.DestructiveButton("Clear stored API key", (_, _) =>
        {
            _clearCredential = true;
            _credentialEdited = false;
            SetCredentialText("");
            _status.Text = "The key will be removed when you save.";
        });

        var content = new VerticalStackLayout { Padding = 16, Spacing = 8 };
        content.Children.Add(Ui.Heading("Settings"));
        content.Children.Add(Ui.Heading("Story Flow"));
        content.Children.Add(_turnPipeline);
        content.Children.Add(new Label
        {
            Text = "2 calls separate draft and state. 3Ã¢â‚¬â€œ5 calls add adjudication, planning, and a plan critic. 7 calls add Story Bible, event, and condition/summary analysis; its parallel variant is faster. 8 calls also revises the prose. More calls cost more.",
            FontSize = 12
        });
        content.Children.Add(Ui.SecondaryButton("Configure Pipeline Calls", async (_, _) =>
            await Navigation.PushAsync(new PipelineSettingsPage(_app, TurnPipelineCalls.For(SelectedPipeline())))));
        content.Children.Add(Ui.SecondaryButton("Manage API Connections", async (_, _) =>
            await Navigation.PushAsync(new ConnectionProfilesPage(_app))));

        var memory = Section(content, "Context & Memory", expanded: false);
        memory.Children.Add(Field("Recent turns in context", _recentTurns));
        memory.Children.Add(Field("Maximum Story Bible entries", _maxEntries));
        memory.Children.Add(Field("Maximum Planned Events", _maxPlannedEvents));
        Add(memory, "Bible entry character limit", "bibleEntry");
        Add(memory, "Bible total character limit", "bibleTotal");
        Add(memory, "Bible capacity warning percent", "bibleWarning");
        Add(memory, "Planned Event entry character limit", "plannedEventEntry");
        Add(memory, "Planned Events total character limit", "plannedEventTotal");
        Add(memory, "Planned Events capacity warning percent", "plannedEventWarning");
        Add(memory, "Story summary characters", "storySummary");

        var narration = Section(content, "Narration & Player Input", expanded: false);
        Add(narration, "Player action characters", "action");
        Add(narration, "Narration characters", "narration");
        Add(narration, "Minimum suggested actions", "suggestedMin");
        Add(narration, "Maximum suggested actions", "suggestedCount");
        Add(narration, "Suggested action characters", "suggestedLength");
        Add(narration, "Minimum paragraphs per response", "paragraphsMin");
        Add(narration, "Maximum paragraphs per response", "paragraphsMax");
        Add(narration, "Minimum sentences per paragraph", "sentencesMin");
        Add(narration, "Maximum sentences per paragraph", "sentencesMax");

        var structure = Section(content, "Story Structure", expanded: false);
        Add(structure, "Title characters", "title");
        Add(structure, "Label characters", "label");
        Add(structure, "Story Definition Prompt / Story Prompt characters", "prompt");
        Add(structure, "Bible category characters", "category");
        Add(structure, "Bible name characters", "name");
        Add(structure, "Bible updates per response", "updates");
        Add(structure, "Planned Event description characters", "plannedEventDescription");
        Add(structure, "Planned Event condition characters", "plannedEventCondition");
        Add(structure, "Planned Event updates per response", "plannedEventUpdates");
        Add(structure, "Victory/Loss Conditions per list", "conditionCount");
        Add(structure, "Condition description characters", "conditionDescription");

        var safety = Section(content, "Advanced Safety", expanded: false);
        Add(safety, "Maximum API response size (bytes)", "responseBytes");
        safety.Children.Add(new Label { Text = "Rejects unexpectedly large provider responses before they use excessive memory.", FontSize = 12 });

        var logging = Section(content, "Diagnostics", expanded: false);
        logging.Children.Add(new Label { Text = "Log level" });
        logging.Children.Add(_logLevel);
        logging.Children.Add(new Label { Text = "default Information", FontSize = 11, TextColor = Colors.Gray });
        logging.Children.Add(new Label
        {
            Text = "Logs are rolling JSON-lines files in the app's private data folder. Trace includes complete LLM request and response bodies and may contain private story and player content. API credentials are never logged.",
            FontSize = 12
        });

        content.Children.Add(Ui.Buttons(
            Ui.Button("Save", Save),
            Ui.SecondaryButton("Reset defaults", Reset)));
        content.Children.Add(Ui.SecondaryButton("Manage Trash", async (_, _) => await Navigation.PushModalAsync(new NavigationPage(new TrashPage(_trash)))));
        content.Children.Add(_status);
        Content = new ScrollView { Content = content };
    }

    private static VerticalStackLayout Section(Layout parent, string title, bool expanded)
    {
        var body = new VerticalStackLayout { Spacing = 6, IsVisible = expanded, Margin = new Thickness(8, 0, 0, 12) };
        var arrow = new Label { Text = expanded ? "â–¾" : "â–¸", FontAttributes = FontAttributes.Bold, WidthRequest = 18 };
        var heading = new Label { Text = title, FontAttributes = FontAttributes.Bold, FontSize = 16 };
        var header = new HorizontalStackLayout { Spacing = 6, Margin = new Thickness(0, 8, 0, 4), Children = { arrow, heading } };
        header.GestureRecognizers.Add(new TapGestureRecognizer
        {
            Command = new Command(() =>
            {
                body.IsVisible = !body.IsVisible;
                arrow.Text = body.IsVisible ? "â–¾" : "â–¸";
            })
        });
        parent.Children.Add(header);
        parent.Children.Add(body);
        return body;
    }

    private TurnPipelineMode SelectedPipeline() => _turnPipeline.SelectedIndex switch
    {
        0 => TurnPipelineMode.OneCall, 1 => TurnPipelineMode.TwoCalls, 2 => TurnPipelineMode.ThreeCalls,
        3 => TurnPipelineMode.FourCalls, 4 => TurnPipelineMode.FiveCalls, 5 => TurnPipelineMode.SevenCalls,
        6 => TurnPipelineMode.SevenCallsParallel, 7 => TurnPipelineMode.EightCalls, _ => TurnPipelineMode.FourCalls
    };

    PendingOperationState? IPendingOperationPage.PendingOperation => _pendingOperation;
    bool IInFlightRequestPage.HasInFlightRequest => _request is not null;
    async Task IInFlightRequestPage.CancelInFlightRequestAsync(bool preserveInterruptedMarker)
    {
        var marker = preserveInterruptedMarker ? _pendingOperation : null;
        _request?.Cancel();
        await Ui.WaitWhileAsync(() => _request is not null, TimeSpan.FromSeconds(5));
        if (marker is not null) _pendingOperation = marker;
    }

    internal void RestoreInterruptedOperation(PendingOperationState? operation)
    {
        if (operation?.Type is not (PendingOperationType.DiscoverModels or PendingOperationType.TestApiConnection)) return;
        _pendingOperation = null;
        _status.Text = operation.Type == PendingOperationType.DiscoverModels
            ? "The previous model discovery was interrupted. Settings were preserved; choose Load Models to retry."
            : "The previous connection test was interrupted. Settings were preserved; choose Test Connection to retry.";
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        // TabbedPage raises OnAppearing on every tab switch, not just the first time this page is
        // shown, so this must only load once - otherwise switching tabs away and back silently
        // discards whatever the user was in the middle of typing.
        if (_loaded) return;
        try
        {
            Load(await _app.GetSettingsAsync());
            ShowCredentialPresence(await _app.HasApiCredentialAsync());
            _loaded = true;
        }
        catch (Exception ex) { await Ui.Error(this, ex); }
    }

    private async void Save(object? sender, EventArgs e)
    {
        try
        {
            var settings = await BuildAsync();
            if (!await TrySaveSettingsAsync(settings)) return;
            _status.Text = "Settings saved.";
        }
        catch (Exception ex) { await Ui.Error(this, ex); }
    }

    private async void Test(object? sender, EventArgs e)
    {
        if (_request is not null) return;
        try
        {
            _request = new();
            var settings = await BuildAsync();
            if (!await TrySaveSettingsAsync(settings, _request.Token)) return;
            _pendingOperation = new(Guid.NewGuid(), PendingOperationType.TestApiConnection, null, null, DateTimeOffset.UtcNow);
            await _tabs.SaveWorkspaceNowAsync();
            var result = await _app.TestConnectionAsync(_request.Token);
            _status.Text = result.Success
                ? string.IsNullOrWhiteSpace(settings.ModelId)
                    ? $"Connected. {result.Models.Count} model(s) discovered."
                    : $"Connected. Structured output: {result.Capabilities.StructuredOutputTier}."
                : result.Error;
        }
        catch (OperationCanceledException) { _status.Text = "Connection test cancelled."; }
        catch (Exception ex) { await Ui.Error(this, ex); }
        finally
        {
            _pendingOperation = null;
            _request?.Dispose();
            _request = null;
            await _tabs.SaveWorkspaceNowAsync();
        }
    }

    private async void DiscoverModels(object? sender, EventArgs e)
    {
        if (_request is not null) return;
        try
        {
            _request = new();
            var settings = await BuildAsync();
            if (settings.BaseUrl is null)
                throw new NarratorException("Enter an API base URL before loading models.");
            if (!await TrySaveSettingsAsync(settings, _request.Token)) return;
            _pendingOperation = new(Guid.NewGuid(), PendingOperationType.DiscoverModels, null, null, DateTimeOffset.UtcNow);
            await _tabs.SaveWorkspaceNowAsync();
            var models = await _app.DiscoverModelsAsync(_request.Token);
            _discoveredModels.ItemsSource = models.ToArray();
            var selectedModel = models.FirstOrDefault(x =>
                string.Equals(x, _model.Text?.Trim(), StringComparison.Ordinal));
            if (selectedModel is not null) _discoveredModels.SelectedItem = selectedModel;
            _status.Text = models.Count == 0
                ? "The provider returned no models. Enter a model ID manually."
                : $"Loaded {models.Count} model{(models.Count == 1 ? "" : "s")}. Select one before testing the connection.";
        }
        catch (OperationCanceledException) { _status.Text = "Model discovery cancelled."; }
        catch (Exception ex) { await Ui.Error(this, ex); }
        finally
        {
            _pendingOperation = null;
            _request?.Dispose();
            _request = null;
            await _tabs.SaveWorkspaceNowAsync();
        }
    }

    private void Reset(object? sender, EventArgs e) => Load(NarratorDefaults.Create());

    private async Task<ApiConnectionSettings> BuildAsync()
    {
        var current = await _app.GetSettingsAsync();
        var baseUrl = Uri.TryCreate(_baseUrl.Text?.Trim(), UriKind.Absolute, out var parsedBaseUrl) ? parsedBaseUrl : null;
        var modelId = string.IsNullOrWhiteSpace(_model.Text) ? null : _model.Text.Trim();
        return current with
        {
            // Connection profiles and their models are configured on dedicated pages. Retain the
            // legacy values solely as a migration fallback for existing workspaces.
            BaseUrl = current.BaseUrl,
            ModelId = current.ModelId,
            RequestTimeout = current.RequestTimeout,
            MaxOutputTokens = current.MaxOutputTokens,
            Parameters = current.Parameters,
            TurnPipeline = _turnPipeline.SelectedIndex switch
            {
                0 => TurnPipelineMode.OneCall,
                1 => TurnPipelineMode.TwoCalls,
                2 => TurnPipelineMode.ThreeCalls,
                3 => TurnPipelineMode.FourCalls,
                4 => TurnPipelineMode.FiveCalls,
                5 => TurnPipelineMode.SevenCalls,
                6 => TurnPipelineMode.SevenCallsParallel,
                7 => TurnPipelineMode.EightCalls,
                _ => TurnPipelineMode.FourCalls
            },
            StoryGeneration = new(
                (int)Parse(_recentTurns, "recent turns"),
                (int)Parse(_maxEntries, "maximum Story Bible entries"),
                Int("bibleEntry"),
                Int("bibleTotal"),
                Int("bibleWarning"),
                (int)Parse(_maxPlannedEvents, "maximum Planned Events"),
                Int("plannedEventEntry"),
                Int("plannedEventTotal"),
                Int("plannedEventWarning")),
            Retry = current.Retry,
            ContentLimits = new(Int("title"), Int("label"), Int("prompt"), Int("action"), Int("narration"),
                Int("suggestedCount"), Int("suggestedLength"),
                Int("category"), Int("name"), Int("updates"),
                Int("plannedEventDescription"), Int("plannedEventCondition"), Int("plannedEventUpdates"),
                Int("conditionCount"), Int("conditionDescription"), Int("storySummary"), Int("responseBytes"))
            {
                MinSuggestedActions = Int("suggestedMin"),
                MinParagraphsPerResponse = Int("paragraphsMin"),
                MaxParagraphsPerResponse = Int("paragraphsMax"),
                MinSentencesPerParagraph = Int("sentencesMin"),
                MaxSentencesPerParagraph = Int("sentencesMax")
            },
            Logging = new((NarratorLogLevel?)_logLevel.SelectedItem
                ?? throw new NarratorException("Select a logging level.")),
            // Compare parsed Uri objects, not strings: current.BaseUrl.ToString() is normalized (trailing
            // slash, casing, escaping) while the entered text isn't, so a string comparison would treat
            // an unchanged URL as "changed" and reset Capabilities to Untested for no reason.
            Capabilities = current.Capabilities
        };
    }

    private async Task<bool> TrySaveSettingsAsync(ApiConnectionSettings proposed, CancellationToken cancellationToken = default)
    {
        var current = await _app.GetSettingsAsync();
        if (proposed.Logging.MinimumLevel == NarratorLogLevel.Trace &&
            current.Logging.MinimumLevel != NarratorLogLevel.Trace &&
            !await DisplayAlertAsync(
                "Enable sensitive Trace logging?",
                "Trace records complete LLM requests and responses, including Story Bibles, player answers, actions, and narration. API credentials remain excluded.",
                "Enable Trace",
                "Cancel"))
            return false;
        if (!await ConfirmBibleLimitImpactAsync(current, proposed)) return false;
        var credential = CredentialChange();
        await _app.SaveSettingsAsync(proposed, credential, cancellationToken);
        CredentialSaved(credential);
        return true;
    }

    private void Load(ApiConnectionSettings settings)
    {
        _baseUrl.Text = settings.BaseUrl?.ToString() ?? "";
        _model.Text = settings.ModelId ?? "";
        _turnPipeline.SelectedIndex = settings.TurnPipeline switch
        {
            TurnPipelineMode.OneCall => 0,
            TurnPipelineMode.TwoCalls => 1,
            TurnPipelineMode.ThreeCalls => 2,
            TurnPipelineMode.FourCalls => 3,
            TurnPipelineMode.FiveCalls => 4,
            TurnPipelineMode.SevenCalls => 5,
            TurnPipelineMode.SevenCallsParallel => 6,
            TurnPipelineMode.EightCalls => 7,
            _ => 3
        };
        _recentTurns.Text = settings.StoryGeneration.RecentTurnCount.ToString(CultureInfo.InvariantCulture);
        _maxEntries.Text = settings.StoryGeneration.MaxStoryBibleEntries.ToString(CultureInfo.InvariantCulture);
        _maxPlannedEvents.Text = settings.StoryGeneration.MaxPlannedEvents.ToString(CultureInfo.InvariantCulture);
        _logLevel.SelectedItem = settings.Logging.MinimumLevel;
        Set("bibleEntry", settings.StoryGeneration.MaxStoryBibleEntryCharacters);
        Set("bibleTotal", settings.StoryGeneration.MaxStoryBibleCharacters);
        Set("bibleWarning", settings.StoryGeneration.StoryBibleWarningPercent);
        Set("plannedEventEntry", settings.StoryGeneration.MaxPlannedEventCharacters);
        Set("plannedEventTotal", settings.StoryGeneration.MaxPlannedEventsCharacters);
        Set("plannedEventWarning", settings.StoryGeneration.PlannedEventsWarningPercent);
        var c = settings.ContentLimits;
        Set("title", c.MaxStoryTitleCharacters);
        Set("label", c.MaxStoryLabelCharacters);
        Set("prompt", c.MaxStoryPromptCharacters);
        Set("action", c.MaxPlayerActionCharacters);
        Set("narration", c.MaxNarrationCharacters);
        Set("suggestedMin", c.MinSuggestedActions);
        Set("suggestedCount", c.MaxSuggestedActions);
        Set("suggestedLength", c.MaxSuggestedActionCharacters);
        Set("paragraphsMin", c.MinParagraphsPerResponse);
        Set("paragraphsMax", c.MaxParagraphsPerResponse);
        Set("sentencesMin", c.MinSentencesPerParagraph);
        Set("sentencesMax", c.MaxSentencesPerParagraph);
        Set("category", c.MaxStoryBibleCategoryCharacters);
        Set("name", c.MaxStoryBibleNameCharacters);
        Set("updates", c.MaxStoryBibleUpdatesPerResponse);
        Set("plannedEventDescription", c.MaxPlannedEventDescriptionCharacters);
        Set("plannedEventCondition", c.MaxPlannedEventConditionCharacters);
        Set("plannedEventUpdates", c.MaxPlannedEventUpdatesPerResponse);
        Set("conditionCount", c.MaxConditions);
        Set("conditionDescription", c.MaxConditionDescriptionCharacters);
        Set("storySummary", c.MaxStorySummaryCharacters);
        Set("responseBytes", c.MaxResponseBodyBytes);
    }

    private string? CredentialChange()
    {
        if (_clearCredential) return "";
        return _credentialEdited ? _apiKey.Text ?? "" : null;
    }

    private void CredentialSaved(string? credential)
    {
        if (credential is not null) _hasStoredCredential = credential.Length > 0;
        _credentialEdited = false;
        _clearCredential = false;
        SetCredentialText(_hasStoredCredential ? StoredCredentialIndicator : "");
    }

    private void ShowCredentialPresence(bool hasStoredCredential)
    {
        _hasStoredCredential = hasStoredCredential;
        _credentialEdited = false;
        _clearCredential = false;
        SetCredentialText(hasStoredCredential ? StoredCredentialIndicator : "");
    }

    private void SetCredentialText(string text)
    {
        _updatingCredentialDisplay = true;
        try { _apiKey.Text = text; }
        finally { _updatingCredentialDisplay = false; }
    }

    private void Add(Layout content, string label, string key)
    {
        var entry = Numeric();
        _fields[key] = entry;
        content.Children.Add(new Label { Text = label });
        content.Children.Add(entry);
        if (Help.TryGetValue(key, out var help))
            content.Children.Add(new Label { Text = help, FontSize = 11, TextColor = Colors.Gray });
    }

    private void Set(string key, object? value) => _fields[key].Text = value is null ? "" : Convert.ToString(value, CultureInfo.InvariantCulture);
    private double Number(string key) => Parse(_fields[key], key);
    private int Int(string key)
    {
        try { return checked((int)Number(key)); }
        catch (OverflowException) { throw new NarratorException($"Enter a valid {key}."); }
    }
    private TimeSpan Seconds(string key) => TimeSpan.FromSeconds(Number(key));

    private static double Parse(Entry entry, string name) =>
        double.TryParse(entry.Text, CultureInfo.InvariantCulture, out var value) ? value : throw new NarratorException($"Enter a valid {name}.");
    private static double? Optional(Entry entry, string name) =>
        string.IsNullOrWhiteSpace(entry.Text) ? null : Parse(entry, name);

    private async Task<bool> ConfirmBibleLimitImpactAsync(ApiConnectionSettings current, ApiConnectionSettings proposed)
    {
        var bibleLowered = proposed.StoryGeneration.MaxStoryBibleEntries < current.StoryGeneration.MaxStoryBibleEntries ||
            proposed.StoryGeneration.MaxStoryBibleEntryCharacters < current.StoryGeneration.MaxStoryBibleEntryCharacters ||
            proposed.StoryGeneration.MaxStoryBibleCharacters < current.StoryGeneration.MaxStoryBibleCharacters;
        var plannedEventsLowered = proposed.StoryGeneration.MaxPlannedEvents < current.StoryGeneration.MaxPlannedEvents ||
            proposed.StoryGeneration.MaxPlannedEventCharacters < current.StoryGeneration.MaxPlannedEventCharacters ||
            proposed.StoryGeneration.MaxPlannedEventsCharacters < current.StoryGeneration.MaxPlannedEventsCharacters;
        if (!bibleLowered && !plannedEventsLowered) return true;
        var impact = await _app.GetBibleLimitImpactAsync(proposed.StoryGeneration);
        if (impact.StoryDefinitionCount == 0 && impact.StoryStateCount == 0 &&
            impact.PlannedEventDefinitionCount == 0 && impact.PlannedEventStateCount == 0)
            return true;
        return await DisplayAlertAsync(
            "Existing Story Bibles or Planned Events exceed the proposed limits",
            $"{impact.StoryDefinitionCount} Story Definitions and {impact.StoryStateCount} Story States have a Story Bible exceeding the proposed limits; " +
            $"{impact.PlannedEventDefinitionCount} Story Definitions and {impact.PlannedEventStateCount} Story States have Planned Events exceeding the proposed limits. " +
            "They will require increased limits or confirmed automatic culling before generation. Saving does not modify them.",
            "Save Anyway",
            "Cancel");
    }

    private static Entry Numeric() => new() { Keyboard = Keyboard.Numeric };
    private static VerticalStackLayout Field(string label, View control) => new() { Spacing = 2, Children = { new Label { Text = label }, control } };
}
