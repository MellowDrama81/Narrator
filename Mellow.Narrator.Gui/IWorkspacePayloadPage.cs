using Mellow.Narrator.Core;

namespace Mellow.Narrator.Gui;

internal interface IWorkspacePayloadPage
{
    StoryPromptDraft? StoryPromptDraft => null;
    PlayStoryTabState? PlayStoryTabState => null;
    PendingOperationState? PendingOperation => null;
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
