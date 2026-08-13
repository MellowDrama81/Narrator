using Mellow.Narrator.Core;

namespace Mellow.Narrator.Gui;

// All calls in the chosen pipeline are configured together so the route can be reviewed as one flow.
public sealed class PipelineSettingsPage : ContentPage
{
    private readonly INarratorApplication _app;
    private readonly IReadOnlyList<GenerationCall> _calls;
    private readonly Dictionary<GenerationCall, RouteEditor> _editors = [];
    private bool _loaded;

    public PipelineSettingsPage(INarratorApplication app, IReadOnlyList<GenerationCall> calls)
    {
        _app = app;
        _calls = calls.Distinct().ToArray();
        Title = "Pipeline Calls";
        var content = new VerticalStackLayout { Padding = 16, Spacing = 10 };
        content.Children.Add(Ui.Heading("Pipeline Call Configuration"));
        content.Children.Add(new Label { Text = "Choose the connection and model for every call in the selected pipeline. Leave a model blank to inherit the default model." });
        foreach (var call in _calls)
        {
            var editor = new RouteEditor();
            _editors.Add(call, editor);
            content.Children.Add(new Label { Text = CallName(call), FontAttributes = FontAttributes.Bold, Margin = new Thickness(0, 8, 0, 0) });
            content.Children.Add(editor.Connection);
            content.Children.Add(Ui.SecondaryButton("Load available models", async (_, _) => await LoadModelsAsync(editor)));
            content.Children.Add(editor.Model);
            content.Children.Add(Field("Timeout seconds", editor.Timeout));
            content.Children.Add(Field("Maximum output tokens", editor.MaxOutputTokens));
            content.Children.Add(Field("Temperature (blank = provider default)", editor.Temperature));
            content.Children.Add(Field("Top-p (blank = provider default)", editor.TopP));
            content.Children.Add(Field("Reasoning effort (blank = provider default)", editor.ReasoningEffort));
            content.Children.Add(Field("Automatic retries", editor.MaxAutomaticRetries));
            content.Children.Add(Field("Initial retry delay seconds", editor.InitialRetryDelay));
            content.Children.Add(Field("Maximum retry delay seconds", editor.MaxRetryDelay));
            content.Children.Add(Field("Maximum Retry-After seconds", editor.MaxRetryAfter));
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
            await DisplayAlertAsync("Saved", "All selected pipeline call routes have been saved.", "OK");
        }
        catch (Exception ex) { await Ui.Error(this, ex); }
    }

    private async Task LoadModelsAsync(RouteEditor editor)
    {
        try
        {
            var profile = editor.Connection.SelectedItem as ApiConnectionProfile ?? throw new NarratorException("Select a connection before loading models.");
            var models = await _app.DiscoverModelsAsync(profile.Id);
            if (models.Count == 0) throw new NarratorException("This connection returned no models. Enter a model ID manually.");
            editor.Model.SetOptions(models);
            if (!models.Contains(editor.Model.Text)) editor.Model.Text = models[0];
        }
        catch (Exception ex) { await Ui.Error(this, ex); }
    }

    private static string CallName(GenerationCall call) => string.Concat(call.ToString().Select((character, index) => index > 0 && char.IsUpper(character) ? " " + character : character.ToString()));
    private static double Parse(Entry entry, string name) =>
        double.TryParse(entry.Text, System.Globalization.CultureInfo.InvariantCulture, out var value) ? value : throw new NarratorException($"Enter a valid {name}.");
    private static double? Optional(Entry entry, string name) => string.IsNullOrWhiteSpace(entry.Text) ? null : Parse(entry, name);
    private static VerticalStackLayout Field(string label, View control) => new() { Spacing = 2, Children = { new Label { Text = label }, control } };

    private sealed class RouteEditor
    {
        public Picker Connection { get; } = new() { Title = "Connection" };
        public EditableModelComboBox Model { get; } = new();
        public Entry Timeout { get; } = Numeric();
        public Entry MaxOutputTokens { get; } = Numeric();
        public Entry Temperature { get; } = Numeric();
        public Entry TopP { get; } = Numeric();
        public Entry ReasoningEffort { get; } = new();
        public Entry MaxAutomaticRetries { get; } = Numeric();
        public Entry InitialRetryDelay { get; } = Numeric();
        public Entry MaxRetryDelay { get; } = Numeric();
        public Entry MaxRetryAfter { get; } = Numeric();

        private static Entry Numeric() => new() { Keyboard = Keyboard.Numeric };
    }

    // MAUI has no built-in editable picker, so this keeps manual model IDs and loaded choices in one control.
    private sealed class EditableModelComboBox : VerticalStackLayout
    {
        private readonly Entry _entry = new() { Placeholder = "Model ID" };
        private readonly VerticalStackLayout _options = new() { IsVisible = false, Spacing = 0 };

        public string? Text
        {
            get => _entry.Text;
            set => _entry.Text = value;
        }

        public EditableModelComboBox()
        {
            var toggle = Ui.SecondaryButton("v", (_, _) => _options.IsVisible = !_options.IsVisible);
            toggle.WidthRequest = 44;
            toggle.HorizontalOptions = LayoutOptions.End;
            var inputRow = new Grid
            {
                ColumnDefinitions = [new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto)],
                ColumnSpacing = 4
            };
            inputRow.Add(_entry);
            inputRow.Add(toggle, 1);
            Children.Add(inputRow);
            Children.Add(_options);
        }

        public void SetOptions(IEnumerable<string> models)
        {
            _options.Children.Clear();
            foreach (var model in models.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var option = Ui.SecondaryButton(model, (_, _) =>
                {
                    Text = model;
                    _options.IsVisible = false;
                });
                option.HorizontalOptions = LayoutOptions.Fill;
                _options.Children.Add(option);
            }
            _options.IsVisible = _options.Children.Count > 0;
        }
    }
}
