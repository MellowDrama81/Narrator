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
    private readonly Entry _apiKey = new() { Placeholder = "Leave blank to keep stored key", IsPassword = true };
    private readonly Entry _timeout = Numeric();
    private readonly Entry _maxOutput = Numeric();
    private readonly Entry _recentTurns = Numeric();
    private readonly Entry _maxEntries = Numeric();
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
                    Field("Base URL", _baseUrl), Field("Model", _model), Field("API key", _apiKey), clear,
                    Ui.Heading("Generation"),
                    Field("Timeout (seconds)", _timeout), Field("Maximum output tokens", _maxOutput),
                    Field("Recent turns", _recentTurns), Field("Maximum Story Bible entries", _maxEntries),
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
    void IInFlightRequestPage.CancelInFlightRequest() => _request?.Cancel();

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
            var credential = _clearCredential ? "" : string.IsNullOrEmpty(_apiKey.Text) ? null : _apiKey.Text;
            await _app.SaveSettingsAsync(settings, credential, _request.Token);
            _apiKey.Text = "";
            _clearCredential = false;
            _pendingOperation = new(Guid.NewGuid(), PendingOperationType.TestApiConnection, null, null, DateTimeOffset.UtcNow);
            await _tabs.SaveWorkspaceNowAsync();
            var result = await _app.TestConnectionAsync(_request.Token);
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
        _recentTurns.Text = settings.StoryGeneration.RecentTurnCount.ToString(CultureInfo.InvariantCulture);
        _maxEntries.Text = settings.StoryGeneration.MaxStoryBibleEntries.ToString(CultureInfo.InvariantCulture);
    }

    private static double Parse(Entry entry, string name) =>
        double.TryParse(entry.Text, CultureInfo.InvariantCulture, out var value) ? value : throw new NarratorException($"Enter a valid {name}.");
    private static Entry Numeric() => new() { Keyboard = Keyboard.Numeric };
    private static VerticalStackLayout Field(string label, View control) => new() { Spacing = 2, Children = { new Label { Text = label }, control } };
}
