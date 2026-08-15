using Mellow.Narrator.Core;

namespace Mellow.Narrator.Maui;

// Dedicated profile editor. Credentials never enter ApiConnectionSettings; each is saved through the
// application's secure-storage boundary after its profile has been persisted.
public sealed class ConnectionProfilesPage : ContentPage
{
    private readonly INarratorApplication _app;
    private readonly VerticalStackLayout _list = new() { Spacing = 10 };
    private readonly Label _status = new();
    private readonly List<ProfileEditor> _editors = [];

    public ConnectionProfilesPage(INarratorApplication app)
    {
        _app = app;
        Title = "API Connections";
        var content = new VerticalStackLayout { Padding = 16, Spacing = 10 };
        content.Children.Add(Ui.Heading("Saved API Connections"));
        content.Children.Add(new Label { Text = "Each API key is stored securely on this device. Connections can be assigned independently to generation calls." });
        content.Children.Add(_list);
        content.Children.Add(Ui.Buttons(Ui.SecondaryButton("Add connection", (_, _) => Add()), Ui.Button("Save", Save)));
        content.Children.Add(_status);
        Content = new ScrollView { Content = content };
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (_editors.Count != 0) return;
        var settings = await _app.GetSettingsAsync();
        foreach (var profile in settings.Connections) await Add(profile);
        if (_editors.Count == 0) await Add(new(Guid.NewGuid(), "Default connection", settings.BaseUrl));
    }

    private void Add() => _ = Add(new(Guid.NewGuid(), "New connection", null));

    private async Task Add(ApiConnectionProfile profile)
    {
        var editor = new ProfileEditor(profile, await _app.GetConnectionCredentialAsync(profile.Id));
        _editors.Add(editor);
        var card = new VerticalStackLayout { Spacing = 6, Padding = 10, BackgroundColor = Colors.LightGray };
        card.Children.Add(new Label { Text = "Connection", FontAttributes = FontAttributes.Bold });
        card.Children.Add(new Label { Text = "Name" }); card.Children.Add(editor.Name);
        card.Children.Add(new Label { Text = "Base URL" }); card.Children.Add(editor.BaseUrl);
        card.Children.Add(new Label { Text = "API key" }); card.Children.Add(editor.ApiKey);
        card.Children.Add(Ui.SecondaryButton("Test connection", async (_, _) => await TestAsync(editor)));
        card.Children.Add(Ui.DestructiveButton("Remove", (_, _) => { _editors.Remove(editor); _list.Children.Remove(card); }));
        _list.Children.Add(card);
    }

    private async void Save(object? sender, EventArgs e)
    {
        try
        {
            await SaveConnectionsAsync();
            _status.Text = "Connections saved.";
        }
        catch (Exception ex) { await Ui.Error(this, ex); }
    }

    private async Task TestAsync(ProfileEditor editor)
    {
        try
        {
            await SaveConnectionsAsync();
            _status.Text = "Testing connection...";
            var result = await _app.TestConnectionAsync(editor.Id);
            _status.Text = result.Success
                ? result.Capabilities.StructuredOutputTier == StructuredOutputTier.Untested
                    ? $"{editor.Name.Text}: connected. {result.Models.Count} model(s) discovered."
                    : $"{editor.Name.Text}: connected ({result.Capabilities.StructuredOutputTier})."
                : result.Error ?? $"{editor.Name.Text}: connection failed.";
        }
        catch (Exception ex) { await Ui.Error(this, ex); }
    }

    private async Task SaveConnectionsAsync()
    {
        if (_editors.Count == 0) throw new NarratorException("Keep at least one API connection.");
        var current = await _app.GetSettingsAsync();
        var profiles = _editors.Select(editor => new ApiConnectionProfile(editor.Id, editor.Name.Text?.Trim() ?? "", ParseUrl(editor.BaseUrl.Text))).ToArray();
        var routes = current.GenerationCallRoutes.ToDictionary(x => x.Key, x =>
            profiles.Any(profile => profile.Id == x.Value.ConnectionId) ? x.Value : x.Value with { ConnectionId = profiles[0].Id });
        await _app.SaveSettingsAsync(current with { Connections = profiles, GenerationCallRoutes = routes }, null);
        foreach (var editor in _editors) await _app.SaveConnectionCredentialAsync(editor.Id, editor.ApiKey.Text);
    }

    private static Uri? ParseUrl(string? value) => Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var url) ? url : null;

    private sealed class ProfileEditor
    {
        public ProfileEditor(ApiConnectionProfile profile, string? credential)
        {
            Id = profile.Id;
            Name = new Entry { Text = profile.Name };
            BaseUrl = new Entry { Text = profile.BaseUrl?.ToString() ?? "", Placeholder = "https://provider.example/v1" };
            ApiKey = new Entry { Text = credential ?? "", IsPassword = true, Placeholder = "API key" };
        }
        public Guid Id { get; }
        public Entry Name { get; }
        public Entry BaseUrl { get; }
        public Entry ApiKey { get; }
    }
}
