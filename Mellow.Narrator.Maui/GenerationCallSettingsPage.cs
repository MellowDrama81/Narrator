using Mellow.Narrator.Core;

namespace Mellow.Narrator.Maui;

// All calls in the chosen pipeline are configured together so the route can be reviewed as one flow.
public sealed class PipelineSettingsPage : ContentPage
{
    private readonly INarratorApplication _app;
    private readonly IReadOnlyList<GenerationCall> _calls;
    private readonly Dictionary<GenerationCall, RouteEditor> _editors = [];
    private readonly Dictionary<Guid, IReadOnlyList<string>> _modelsByConnection = [];
    private bool _loaded;

    public PipelineSettingsPage(INarratorApplication app, IReadOnlyList<GenerationCall> calls)
    {
        _app = app;
        _calls = calls.Distinct().ToArray();
        Title = "Pipeline Calls";
        var content = new VerticalStackLayout { Padding = 16, Spacing = 10 };
        content.Children.Add(Ui.Heading("Pipeline Call Configuration"));
        content.Children.Add(new Label { Text = "Choose a connection and model for each call. Open Advanced Request Behavior only when a call needs different HTTP settings." });
        foreach (var call in _calls)
        {
            var editor = new RouteEditor();
            _editors.Add(call, editor);
            content.Children.Add(new Label { Text = CallName(call), FontAttributes = FontAttributes.Bold, Margin = new Thickness(0, 8, 0, 0) });
            content.Children.Add(editor.Connection);
            content.Children.Add(Ui.SecondaryButton("Load available models", async (_, _) => await LoadModelsAsync(editor)));
            content.Children.Add(editor.AvailableModels);
            content.Children.Add(editor.Model);
            content.Children.Add(editor.CapabilityStatus);
            var advanced = CollapsibleSection(content, "Advanced Request Behavior");
            advanced.Children.Add(Field("Timeout seconds", editor.Timeout));
            advanced.Children.Add(Field("Maximum output tokens", editor.MaxOutputTokens));
            advanced.Children.Add(Field("Temperature (blank = provider default)", editor.Temperature));
            advanced.Children.Add(Field("Top-p (blank = provider default)", editor.TopP));
            advanced.Children.Add(Field("Reasoning effort (blank = provider default)", editor.ReasoningEffort));
            advanced.Children.Add(Field("Automatic retries", editor.MaxAutomaticRetries));
            advanced.Children.Add(Field("Initial retry delay seconds", editor.InitialRetryDelay));
            advanced.Children.Add(Field("Maximum retry delay seconds", editor.MaxRetryDelay));
            advanced.Children.Add(Field("Maximum Retry-After seconds", editor.MaxRetryAfter));
        }
        content.Children.Add(Ui.Button("Save all pipeline calls", Save));
        Content = new ScrollView { Content = content };
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (_loaded) return;
        var settings = await _app.GetSettingsAsync();
        foreach (var (call, editor) in _editors)
        {
            editor.Connection.ItemsSource = settings.Connections.ToArray();
            editor.Connection.ItemDisplayBinding = new Binding(nameof(ApiConnectionProfile.Name));
            editor.Connection.SelectedIndexChanged += (_, _) => ApplyCachedModels(editor);
            editor.Model.TextChanged += (_, _) => UpdateCapabilityStatus(editor);
            var route = settings.GenerationCallRoutes.TryGetValue(call, out var configured) ? configured : null;
            editor.Connection.SelectedItem = settings.Connections.FirstOrDefault(profile => profile.Id == route?.ConnectionId) ?? settings.Connections.FirstOrDefault();
            editor.Model.Text = route?.ModelId ?? settings.ModelId;
            editor.Timeout.Text = (route?.RequestTimeout ?? settings.RequestTimeout).TotalSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
            editor.MaxOutputTokens.Text = (route?.MaxOutputTokens ?? settings.MaxOutputTokens).ToString(System.Globalization.CultureInfo.InvariantCulture);
            editor.Temperature.Text = (route?.Parameters?.Temperature ?? settings.Parameters.Temperature)?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "";
            editor.TopP.Text = (route?.Parameters?.TopP ?? settings.Parameters.TopP)?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "";
            editor.ReasoningEffort.Text = route?.Parameters?.ReasoningEffort ?? settings.Parameters.ReasoningEffort ?? "";
            var retry = route?.Retry ?? settings.Retry;
            editor.MaxAutomaticRetries.Text = retry.MaxAutomaticRetries.ToString(System.Globalization.CultureInfo.InvariantCulture);
            editor.InitialRetryDelay.Text = retry.InitialDelay.TotalSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
            editor.MaxRetryDelay.Text = retry.MaxDelay.TotalSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
            editor.MaxRetryAfter.Text = retry.MaxRetryAfter.TotalSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        _loaded = true;
    }

    private async void Save(object? sender, EventArgs e)
    {
        try
        {
            var settings = await _app.GetSettingsAsync();
            var routes = settings.GenerationCallRoutes.ToDictionary(x => x.Key, x => x.Value);
            foreach (var (call, editor) in _editors)
            {
                var profile = editor.Connection.SelectedItem as ApiConnectionProfile ?? throw new NarratorException($"Select a connection for {CallName(call)}.");
                routes[call] = new(profile.Id, string.IsNullOrWhiteSpace(editor.Model.Text) ? null : editor.Model.Text.Trim())
                {
                    RequestTimeout = TimeSpan.FromSeconds(Parse(editor.Timeout, $"{CallName(call)} timeout")),
                    MaxOutputTokens = (int)Parse(editor.MaxOutputTokens, $"{CallName(call)} maximum output tokens"),
                    Parameters = new(
                        Optional(editor.Temperature, $"{CallName(call)} temperature"),
                        Optional(editor.TopP, $"{CallName(call)} top-p"),
                        string.IsNullOrWhiteSpace(editor.ReasoningEffort.Text) ? null : editor.ReasoningEffort.Text.Trim()),
                    Retry = new(
                        (int)Parse(editor.MaxAutomaticRetries, $"{CallName(call)} automatic retries"),
                        TimeSpan.FromSeconds(Parse(editor.InitialRetryDelay, $"{CallName(call)} initial retry delay")),
                        TimeSpan.FromSeconds(Parse(editor.MaxRetryDelay, $"{CallName(call)} maximum retry delay")),
                        TimeSpan.FromSeconds(Parse(editor.MaxRetryAfter, $"{CallName(call)} maximum Retry-After")))
                };
            }
            await _app.SaveSettingsAsync(settings with { GenerationCallRoutes = routes }, null);
            var tested = new HashSet<(Guid ConnectionId, string ModelId)>();
            foreach (var route in routes.Values)
                if (route.ConnectionId is { } connectionId && !string.IsNullOrWhiteSpace(route.ModelId))
                    tested.Add((connectionId, route.ModelId));
            foreach (var (connectionId, modelId) in tested)
            {
                var connection = settings.Connections.FirstOrDefault(candidate => candidate.Id == connectionId);
                if (connection is null || !connection.ModelCapabilities.ContainsKey(modelId))
                    await _app.TestConnectionAsync(connectionId, modelId);
            }
            await DisplayAlertAsync("Saved", "Pipeline calls saved and each selected model has been tested.", "OK");
            foreach (var editor in _editors.Values) UpdateCapabilityStatus(editor);
        }
        catch (Exception ex) { await Ui.Error(this, ex); }
    }

    private async Task LoadModelsAsync(RouteEditor editor)
    {
        try
        {
            var profile = editor.Connection.SelectedItem as ApiConnectionProfile ?? throw new NarratorException("Select a connection before loading models.");
            if (!_modelsByConnection.TryGetValue(profile.Id, out var models))
            {
                models = await _app.DiscoverModelsAsync(profile.Id);
                _modelsByConnection[profile.Id] = models;
            }
            if (models.Count == 0) throw new NarratorException("This connection returned no models. Enter a model ID manually.");
            foreach (var otherEditor in _editors.Values.Where(candidate => (candidate.Connection.SelectedItem as ApiConnectionProfile)?.Id == profile.Id))
            {
                otherEditor.AvailableModels.ItemsSource = models.ToArray();
                if (!models.Contains(otherEditor.Model.Text)) otherEditor.AvailableModels.SelectedItem = models[0];
            }
        }
        catch (Exception ex) { await Ui.Error(this, ex); }
    }

    private static string CallName(GenerationCall call) => string.Concat(call.ToString().Select((character, index) => index > 0 && char.IsUpper(character) ? " " + character : character.ToString()));
    private static double Parse(Entry entry, string name) =>
        double.TryParse(entry.Text, System.Globalization.CultureInfo.InvariantCulture, out var value) ? value : throw new NarratorException($"Enter a valid {name}.");
    private static double? Optional(Entry entry, string name) => string.IsNullOrWhiteSpace(entry.Text) ? null : Parse(entry, name);
    private static VerticalStackLayout Field(string label, View control) => new() { Spacing = 2, Children = { new Label { Text = label }, control } };
    private static VerticalStackLayout CollapsibleSection(Layout parent, string title)
    {
        var body = new VerticalStackLayout { Spacing = 6, IsVisible = false, Margin = new Thickness(8, 0, 0, 8) };
        var button = Ui.SecondaryButton(title, (_, _) => body.IsVisible = !body.IsVisible);
        parent.Children.Add(button);
        parent.Children.Add(body);
        return body;
    }

    private sealed class RouteEditor
    {
        public Picker Connection { get; } = new() { Title = "Connection" };
        public Picker AvailableModels { get; } = new() { Title = "Loaded models" };
        public Entry Model { get; } = new() { Placeholder = "Model ID (or select above)" };
        public Label CapabilityStatus { get; } = new() { FontSize = 12 };
        public Entry Timeout { get; } = Numeric();
        public Entry MaxOutputTokens { get; } = Numeric();
        public Entry Temperature { get; } = Numeric();
        public Entry TopP { get; } = Numeric();
        public Entry ReasoningEffort { get; } = new();
        public Entry MaxAutomaticRetries { get; } = Numeric();
        public Entry InitialRetryDelay { get; } = Numeric();
        public Entry MaxRetryDelay { get; } = Numeric();
        public Entry MaxRetryAfter { get; } = Numeric();

        public RouteEditor()
        {
            AvailableModels.SelectedIndexChanged += (_, _) =>
            {
                if (AvailableModels.SelectedItem is string model) Model.Text = model;
            };
        }

        private static Entry Numeric() => new() { Keyboard = Keyboard.Numeric };
    }

    private void ApplyCachedModels(RouteEditor editor)
    {
        if (editor.Connection.SelectedItem is not ApiConnectionProfile profile || !_modelsByConnection.TryGetValue(profile.Id, out var models))
        {
            editor.AvailableModels.ItemsSource = null;
            return;
        }
        editor.AvailableModels.ItemsSource = models.ToArray();
        var selected = models.FirstOrDefault(model => string.Equals(model, editor.Model.Text, StringComparison.Ordinal));
        if (selected is not null) editor.AvailableModels.SelectedItem = selected;
        UpdateCapabilityStatus(editor);
    }

    private async void UpdateCapabilityStatus(RouteEditor editor)
    {
        if (editor.Connection.SelectedItem is not ApiConnectionProfile profile || string.IsNullOrWhiteSpace(editor.Model.Text))
        {
            editor.CapabilityStatus.Text = "Model capability: not selected";
            return;
        }
        var settings = await _app.GetSettingsAsync();
        profile = settings.Connections.FirstOrDefault(connection => connection.Id == profile.Id) ?? profile;
        editor.CapabilityStatus.Text = profile.ModelCapabilities.TryGetValue(editor.Model.Text.Trim(), out var capability)
            ? $"Model capability: {capability.StructuredOutputTier} (tested {capability.TestedAtUtc?.ToLocalTime():g})"
            : "Model capability: untested â€” it will be tested when you save.";
    }
}
