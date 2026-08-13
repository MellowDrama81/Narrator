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
                routes[call] = new(profile.Id, string.IsNullOrWhiteSpace(editor.Model.Text) ? null : editor.Model.Text.Trim());
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

    private sealed class RouteEditor
    {
        public Picker Connection { get; } = new() { Title = "Connection" };
        public EditableModelComboBox Model { get; } = new();
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
