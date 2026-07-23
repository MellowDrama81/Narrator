using System.Globalization;
using Mellow.Narrator.Core;

namespace Mellow.Narrator.Gui;

public sealed class SettingsPage : ContentPage, IWorkspacePayloadPage, IInFlightRequestPage
{
    private readonly INarratorApplication _app;
    private readonly ITrashStore _trash;
    private readonly MainTabbedPage _tabs;
    private readonly Entry _baseUrl = new() { Placeholder = "https://provider.example/v1" };
    private readonly Entry _model = new() { Placeholder = "Model ID" };
    private readonly Picker _discoveredModels = new() { Title = "Discovered models" };
    private readonly Entry _apiKey = new() { Placeholder = "Leave blank to keep stored key", IsPassword = true };
    private readonly Entry _timeout = Numeric();
    private readonly Entry _maxOutput = Numeric();
    private readonly Entry _recentTurns = Numeric();
    private readonly Entry _maxEntries = Numeric();
    private readonly Entry _temperature = Numeric();
    private readonly Entry _topP = Numeric();
    private readonly Entry _reasoning = new();
    private readonly Label _status = new();
    private bool _clearCredential;
    private CancellationTokenSource? _request;
    private PendingOperationState? _pendingOperation;

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
        var clear = Ui.Button("Clear stored API key", (_, _) => { _clearCredential = true; _apiKey.Text = ""; _status.Text = "The key will be removed when you save."; });
        Content = new ScrollView
        {
            Content = new VerticalStackLayout
            {
                Padding = 16,
                Spacing = 8,
                Children =
                {
                    Ui.Heading("API Connection"),
                    Field("Base URL", _baseUrl), Field("Model ID", _model), _discoveredModels,
                    new Label { Text = "Changing the model applies to every subsequent LLM request, including existing stories.", FontSize = 12 },
                    Field("API key", _apiKey), clear,
                    Ui.Heading("Generation"),
                    Field("Timeout seconds (default 120; range 10–900)", _timeout),
                    Field("Maximum output tokens (default 4096; range 256–131072)", _maxOutput),
                    Field("Temperature (blank; range 0–2)", _temperature),
                    Field("Top-p (blank; range 0–1)", _topP),
                    Field("Reasoning effort (blank = provider default)", _reasoning),
                    Field("Recent turns (default 8; range 0–100)", _recentTurns),
                    Field("Maximum Story Bible entries (default 200; range 1–2000)", _maxEntries),
                    Ui.Buttons(Ui.Button("Save", Save), Ui.Button("Test Connection", Test), Ui.Button("Reset defaults", Reset)),
                    Ui.Buttons(
                        Ui.Button("Advanced Settings", async (_, _) => await Navigation.PushModalAsync(new NavigationPage(new AdvancedSettingsPage(_app)))),
                        Ui.Button("Manage Trash", async (_, _) => await Navigation.PushModalAsync(new NavigationPage(new TrashPage(_trash))))),
                    _status
                }
            }
        };
    }

    PendingOperationState? IWorkspacePayloadPage.PendingOperation => _pendingOperation;
    bool IInFlightRequestPage.HasInFlightRequest => _request is not null;
    async Task IInFlightRequestPage.CancelInFlightRequestAsync(bool preserveInterruptedMarker)
    {
        var marker = preserveInterruptedMarker ? _pendingOperation : null;
        _request?.Cancel();
        while (_request is not null) await Task.Delay(20);
        if (marker is not null) _pendingOperation = marker;
    }

    internal void RestoreInterruptedOperation(PendingOperationState? operation)
    {
        if (operation?.Type != PendingOperationType.TestApiConnection) return;
        _pendingOperation = null;
        _status.Text = "The previous connection test was interrupted. Settings were preserved; choose Test Connection to retry.";
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        try { Load(await _app.GetSettingsAsync()); } catch (Exception ex) { await Ui.Error(this, ex); }
    }

    private async void Save(object? sender, EventArgs e)
    {
        try
        {
            var settings = await BuildAsync();
            if (!await ConfirmBibleLimitImpactAsync(settings)) return;
            var credential = _clearCredential ? "" : string.IsNullOrEmpty(_apiKey.Text) ? null : _apiKey.Text;
            await _app.SaveSettingsAsync(settings, credential);
            _apiKey.Text = "";
            _clearCredential = false;
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
            if (!await ConfirmBibleLimitImpactAsync(settings)) return;
            var credential = _clearCredential ? "" : string.IsNullOrEmpty(_apiKey.Text) ? null : _apiKey.Text;
            await _app.SaveSettingsAsync(settings, credential, _request.Token);
            _apiKey.Text = "";
            _clearCredential = false;
            _pendingOperation = new(Guid.NewGuid(), PendingOperationType.TestApiConnection, null, null, DateTimeOffset.UtcNow);
            await _tabs.SaveWorkspaceNowAsync();
            var result = await _app.TestConnectionAsync(_request.Token);
            _discoveredModels.ItemsSource = result.Models.ToArray();
            _status.Text = result.Success
                ? $"Connected. Structured output: {result.Capabilities.StructuredOutputTier}. Models found: {result.Models.Count}."
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

    private void Reset(object? sender, EventArgs e) => Load(NarratorDefaults.Create());

    private async Task<ApiConnectionSettings> BuildAsync()
    {
        var current = await _app.GetSettingsAsync();
        return current with
        {
            BaseUrl = Uri.TryCreate(_baseUrl.Text?.Trim(), UriKind.Absolute, out var uri) ? uri : null,
            ModelId = string.IsNullOrWhiteSpace(_model.Text) ? null : _model.Text.Trim(),
            RequestTimeout = TimeSpan.FromSeconds(Parse(_timeout, "timeout")),
            MaxOutputTokens = (int)Parse(_maxOutput, "maximum output tokens"),
            Parameters = new(
                Optional(_temperature, "temperature"),
                Optional(_topP, "top-p"),
                string.IsNullOrWhiteSpace(_reasoning.Text) ? null : _reasoning.Text),
            StoryGeneration = current.StoryGeneration with
            {
                RecentTurnCount = (int)Parse(_recentTurns, "recent turns"),
                MaxStoryBibleEntries = (int)Parse(_maxEntries, "maximum Story Bible entries")
            },
            Capabilities = current.BaseUrl?.ToString() == _baseUrl.Text?.Trim() && current.ModelId == _model.Text?.Trim()
                ? current.Capabilities : new(false, StructuredOutputTier.Untested, null, null)
        };
    }

    private void Load(ApiConnectionSettings settings)
    {
        _baseUrl.Text = settings.BaseUrl?.ToString() ?? "";
        _model.Text = settings.ModelId ?? "";
        _timeout.Text = settings.RequestTimeout.TotalSeconds.ToString(CultureInfo.InvariantCulture);
        _maxOutput.Text = settings.MaxOutputTokens.ToString(CultureInfo.InvariantCulture);
        _temperature.Text = settings.Parameters.Temperature?.ToString(CultureInfo.InvariantCulture) ?? "";
        _topP.Text = settings.Parameters.TopP?.ToString(CultureInfo.InvariantCulture) ?? "";
        _reasoning.Text = settings.Parameters.ReasoningEffort ?? "";
        _recentTurns.Text = settings.StoryGeneration.RecentTurnCount.ToString(CultureInfo.InvariantCulture);
        _maxEntries.Text = settings.StoryGeneration.MaxStoryBibleEntries.ToString(CultureInfo.InvariantCulture);
    }

    private static double Parse(Entry entry, string name) =>
        double.TryParse(entry.Text, CultureInfo.InvariantCulture, out var value) ? value : throw new NarratorException($"Enter a valid {name}.");
    private static double? Optional(Entry entry, string name) =>
        string.IsNullOrWhiteSpace(entry.Text) ? null : Parse(entry, name);

    private async Task<bool> ConfirmBibleLimitImpactAsync(ApiConnectionSettings proposed)
    {
        var current = await _app.GetSettingsAsync();
        var lowered = proposed.StoryGeneration.MaxStoryBibleEntries < current.StoryGeneration.MaxStoryBibleEntries ||
            proposed.StoryGeneration.MaxStoryBibleEntryCharacters < current.StoryGeneration.MaxStoryBibleEntryCharacters ||
            proposed.StoryGeneration.MaxStoryBibleCharacters < current.StoryGeneration.MaxStoryBibleCharacters;
        if (!lowered) return true;
        var impact = await _app.GetBibleLimitImpactAsync(proposed.StoryGeneration);
        if (impact.StoryDefinitionCount == 0 && impact.StoryStateCount == 0) return true;
        return await DisplayAlertAsync(
            "Existing Story Bibles exceed the proposed limits",
            $"{impact.StoryDefinitionCount} Story Definitions and {impact.StoryStateCount} Story States will require increased limits or confirmed automatic culling before generation. Saving does not modify them.",
            "Save Anyway",
            "Cancel");
    }
    private static Entry Numeric() => new() { Keyboard = Keyboard.Numeric };
    private static VerticalStackLayout Field(string label, View control) => new() { Spacing = 2, Children = { new Label { Text = label }, control } };
}
