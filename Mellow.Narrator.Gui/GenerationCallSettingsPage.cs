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
        content.Children.Add(new Label { Text = "Choose a connection and model for each call. Open Advanced Request Behavior only when a call needs different HTTP settings." });
        foreach (var call in _calls)
        {
            var editor = new RouteEditor();
            _editors.Add(call, editor);
            content.Children.Add(new Label { Text = CallName(call), FontAttributes = FontAttributes.Bold, Margin = new Thickness(0, 8, 0, 0) });
            content.Children.Add(editor.Connection);
            content.Children.Add(Ui.SecondaryButton("Load available models", async (_, _) => await LoadModelsAsync(editor)));
            content.Children.Add(editor.Model);
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
            editor.Model.SetModels(models);
            if (!models.Contains(editor.Model.Text)) editor.Model.Text = models[0];
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
        public ModelSelector Model { get; } = new();
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

    // MAUI has no cross-platform editable picker. Keep typing in-place and show choices in a modal picker.
    private sealed class ModelSelector : Grid
    {
        private readonly Entry _entry = new() { Placeholder = "Model ID" };
        private IReadOnlyList<string> _models = [];

        public string? Text
        {
            get => _entry.Text;
            set => _entry.Text = value;
        }

        public ModelSelector()
        {
            ColumnDefinitions = [new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto)];
            ColumnSpacing = 4;
            var choose = Ui.SecondaryButton("Choose", async (_, _) =>
            {
                if (_models.Count == 0)
                {
                    await Application.Current!.Windows[0].Page!.DisplayAlertAsync("No models loaded", "Load available models first, or type a model ID manually.", "OK");
                    return;
                }
                var picker = new ModelPickerPage(_models, Text);
                await Application.Current!.Windows[0].Page!.Navigation.PushModalAsync(new NavigationPage(picker));
                var selected = await picker.Selection;
                if (selected is not null) Text = selected;
            });
            choose.WidthRequest = 82;
            Children.Add(_entry);
            Children.Add(choose);
            Grid.SetColumn(choose, 1);
        }

        public void SetModels(IEnumerable<string> models)
        {
            _models = models.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(model => model).ToArray();
        }
    }

    private sealed class ModelPickerPage : ContentPage
    {
        private readonly TaskCompletionSource<string?> _selection = new();
        private readonly VerticalStackLayout _options = new() { Spacing = 2 };
        private readonly IReadOnlyList<string> _models;
        public Task<string?> Selection => _selection.Task;

        public ModelPickerPage(IReadOnlyList<string> models, string? currentModel)
        {
            _models = models;
            Title = "Choose Model";
            var search = new SearchBar { Placeholder = "Search loaded models", Text = currentModel };
            search.TextChanged += (_, _) => ShowMatches(search.Text);
            Content = new VerticalStackLayout
            {
                Padding = 16,
                Spacing = 10,
                Children =
                {
                    search,
                    new ScrollView { Content = _options, VerticalOptions = LayoutOptions.Fill },
                    Ui.SecondaryButton("Cancel", async (_, _) => await CloseAsync(null))
                }
            };
            ShowMatches(search.Text);
        }

        protected override void OnDisappearing()
        {
            base.OnDisappearing();
            _selection.TrySetResult(null);
        }

        private void ShowMatches(string? query)
        {
            _options.Children.Clear();
            foreach (var model in _models.Where(model => string.IsNullOrWhiteSpace(query) || model.Contains(query, StringComparison.OrdinalIgnoreCase)))
                _options.Children.Add(Ui.SecondaryButton(model, async (_, _) => await CloseAsync(model)));
        }

        private async Task CloseAsync(string? model)
        {
            _selection.TrySetResult(model);
            await Navigation.PopModalAsync();
        }
    }
}
