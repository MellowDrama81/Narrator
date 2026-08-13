namespace Mellow.Narrator.Maui;

public partial class App : Application
{
    private readonly MainTabbedPage _mainPage;

    public App(MainTabbedPage mainPage)
    {
        InitializeComponent();
        _mainPage = mainPage;
    }

    protected override Window CreateWindow(IActivationState? activationState)
    {
        var window = new Window(_mainPage);
        window.Stopped += async (_, _) => await _mainPage.CancelInFlightRequestsAsync();
        window.Destroying += async (_, _) => await _mainPage.CancelInFlightRequestsAsync();
        return window;
    }
}
