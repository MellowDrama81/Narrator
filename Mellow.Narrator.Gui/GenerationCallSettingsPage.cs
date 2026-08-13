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

    private static string CallName(GenerationCall call) => string.Concat(call.ToString().Select((character, index) => index > 0 && char.IsUpper(character) ? " " + character : character.ToString()));

    private sealed class RouteEditor
    {
        public Picker Connection { get; } = new() { Title = "Connection" };
        public Entry Model { get; } = new() { Placeholder = "Model ID" };
    }
}
