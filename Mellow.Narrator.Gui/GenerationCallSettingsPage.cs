using Mellow.Narrator.Core;

namespace Mellow.Narrator.Gui;

// A page per selected generation call keeps routing discoverable even when a larger pipeline is used.
public sealed class GenerationCallSettingsPage : ContentPage
{
    private readonly INarratorApplication _app;
    private readonly GenerationCall _call;
    private readonly Picker _connection = new() { Title = "Connection" };
    private readonly Entry _model = new() { Placeholder = "Model ID" };

    public GenerationCallSettingsPage(INarratorApplication app, GenerationCall call)
    {
        _app = app;
        _call = call;
        Title = CallName(call);
        Content = new VerticalStackLayout
        {
            Padding = 16, Spacing = 10,
            Children =
            {
                Ui.Heading(CallName(call)),
                new Label { Text = "Select the connection and model used only for this LLM call. Leave the model blank to inherit the default model." },
                new Label { Text = "Connection" }, _connection,
                new Label { Text = "Model" }, _model,
                Ui.Button("Save", Save)
            }
        };
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        var settings = await _app.GetSettingsAsync();
        _connection.ItemsSource = settings.Connections.ToArray();
        _connection.ItemDisplayBinding = new Binding(nameof(ApiConnectionProfile.Name));
        var route = settings.GenerationCallRoutes.TryGetValue(_call, out var configured) ? configured : null;
        _connection.SelectedItem = settings.Connections.FirstOrDefault(profile => profile.Id == route?.ConnectionId) ?? settings.Connections.FirstOrDefault();
        _model.Text = route?.ModelId ?? settings.ModelId;
    }

    private async void Save(object? sender, EventArgs e)
    {
        try
        {
            var profile = _connection.SelectedItem as ApiConnectionProfile ?? throw new NarratorException("Select a connection.");
            var settings = await _app.GetSettingsAsync();
            var routes = settings.GenerationCallRoutes.ToDictionary(x => x.Key, x => x.Value);
            routes[_call] = new(profile.Id, string.IsNullOrWhiteSpace(_model.Text) ? null : _model.Text.Trim());
            await _app.SaveSettingsAsync(settings with { GenerationCallRoutes = routes }, null);
            await DisplayAlertAsync("Saved", "This call's connection and model have been saved.", "OK");
        }
        catch (Exception ex) { await Ui.Error(this, ex); }
    }

    public static string CallName(GenerationCall call) => string.Concat(call.ToString().Select((character, index) => index > 0 && char.IsUpper(character) ? " " + character : character.ToString()));
}
