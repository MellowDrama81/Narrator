using Mellow.Narrator.Core;

namespace Mellow.Narrator.Gui;

internal interface IStoryPromptDraftPage
{
    StoryPromptDraft? StoryPromptDraft { get; }
}

internal interface IPlayStoryTabStatePage
{
    PlayStoryTabState? PlayStoryTabState { get; }
}

internal interface IPendingOperationPage
{
    PendingOperationState? PendingOperation { get; }
}

internal interface ICloseGuardPage
{
    Task<bool> CanCloseAsync();
}

internal interface IInFlightRequestPage
{
    bool HasInFlightRequest { get; }
    Task CancelInFlightRequestAsync(bool preserveInterruptedMarker = false);
}
