namespace Mellow.Narrator.Gui;

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
        window.Stopped += (_, _) => _mainPage.CancelInFlightRequests();
        window.Destroying += (_, _) => _mainPage.CancelInFlightRequests();
        return window;
    }
}
