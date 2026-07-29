using Mellow.Narrator.Core;

namespace Mellow.Narrator.Tests;

public sealed class ImportExportProcessorTests
{
    [Fact]
    public void StateCopy_RemapsEntriesAndTurnsConsistently()
    {
        var (state, turn) = CreateState();

        var copy = ImportExportProcessor.CopyState(
            state,
            [turn],
            7,
            NarratorDefaults.Create().ContentLimits,
            NarratorDefaults.Create().StoryGeneration);

        Assert.NotEqual(state.Id, copy.State.Id);
        Assert.Equal(7, copy.State.SortOrder);
        var entry = Assert.Single(copy.State.CurrentStoryBible.Entries);
        Assert.NotEqual(state.CurrentStoryBible.Entries[0].Id, entry.Id);
        var copiedTurn = Assert.Single(copy.Turns);
        Assert.Equal(copy.State.Id, copiedTurn.StoryStateId);
        Assert.Equal(entry.Id, Assert.Single(copiedTurn.RelevantStoryBibleEntryIds));
    }

    [Fact]
    public void DefinitionCopy_RejectsNonUtcTimestamp()
    {
        var now = DateTimeOffset.UtcNow;
        var definition = new StoryDefinition(
            Guid.NewGuid(),
            "Story",
            "A sufficiently long prompt for validation.",
            StoryBible.Empty,
            [],
            0,
            now.ToOffset(TimeSpan.FromHours(2)),
            now);

        Assert.Throws<InvalidDataException>(() => ImportExportProcessor.CopyDefinition(
            definition,
            1,
            NarratorDefaults.Create().ContentLimits,
            NarratorDefaults.Create().StoryGeneration));
    }

    [Fact]
    public void DefinitionCopy_RejectsEmptyId()
    {
        var now = DateTimeOffset.UtcNow;
        var definition = new StoryDefinition(
            Guid.Empty, "Story", "A sufficiently long prompt for validation.", StoryBible.Empty, [], 0, now, now);

        Assert.Throws<InvalidDataException>(() => ImportExportProcessor.CopyDefinition(
            definition, 1, NarratorDefaults.Create().ContentLimits, NarratorDefaults.Create().StoryGeneration));
    }

    [Fact]
    public void DefinitionCopy_RejectsOversizedTitle()
    {
        var now = DateTimeOffset.UtcNow;
        var limits = NarratorDefaults.Create().ContentLimits;
        var definition = new StoryDefinition(
            Guid.NewGuid(),
            new string('x', limits.MaxStoryTitleCharacters + 1),
            "A sufficiently long prompt for validation.",
            StoryBible.Empty,
            [],
            0,
            now,
            now);

        Assert.Throws<InvalidDataException>(() => ImportExportProcessor.CopyDefinition(
            definition, 1, limits, NarratorDefaults.Create().StoryGeneration));
    }

    [Fact]
    public void DefinitionCopy_RejectsOversizedStoryPrompt()
    {
        var now = DateTimeOffset.UtcNow;
        var limits = NarratorDefaults.Create().ContentLimits;
        var definition = new StoryDefinition(
            Guid.NewGuid(),
            "Story",
            new string('x', limits.MaxStoryPromptCharacters + 1),
            StoryBible.Empty,
            [],
            0,
            now,
            now);

        Assert.Throws<InvalidDataException>(() => ImportExportProcessor.CopyDefinition(
            definition, 1, limits, NarratorDefaults.Create().StoryGeneration));
    }

    [Fact]
    public void DefinitionCopy_RejectsBibleEntryWithNoFacts()
    {
        var now = DateTimeOffset.UtcNow;
        var entry = new StoryBibleEntry(Guid.NewGuid(), "fact", "Name", [], [], 3, 0);
        var definition = new StoryDefinition(
            Guid.NewGuid(), "Story", "A sufficiently long prompt for validation.", new([entry]), [], 0, now, now);

        Assert.Throws<InvalidDataException>(() => ImportExportProcessor.CopyDefinition(
            definition, 1, NarratorDefaults.Create().ContentLimits, NarratorDefaults.Create().StoryGeneration));
    }

    [Fact]
    public void DefinitionCopy_RejectsBibleEntryWithInvalidImportance()
    {
        var now = DateTimeOffset.UtcNow;
        var entry = new StoryBibleEntry(Guid.NewGuid(), "fact", "Name", ["Content"], [], 6, 0);
        var definition = new StoryDefinition(
            Guid.NewGuid(), "Story", "A sufficiently long prompt for validation.", new([entry]), [], 0, now, now);

        Assert.Throws<InvalidDataException>(() => ImportExportProcessor.CopyDefinition(
            definition, 1, NarratorDefaults.Create().ContentLimits, NarratorDefaults.Create().StoryGeneration));
    }

    [Fact]
    public void DefinitionCopy_RejectsDuplicateBibleEntryIds()
    {
        var now = DateTimeOffset.UtcNow;
        var id = Guid.NewGuid();
        var first = new StoryBibleEntry(id, "fact", "First", ["Content"], [], 3, 0);
        var second = new StoryBibleEntry(id, "fact", "Second", ["Content"], [], 3, 0);
        var definition = new StoryDefinition(
            Guid.NewGuid(), "Story", "A sufficiently long prompt for validation.", new([first, second]), [], 0, now, now);

        Assert.Throws<InvalidDataException>(() => ImportExportProcessor.CopyDefinition(
            definition, 1, NarratorDefaults.Create().ContentLimits, NarratorDefaults.Create().StoryGeneration));
    }

    [Fact]
    public void DefinitionCopy_RejectsBibleExceedingStoryGenerationEntryLimit()
    {
        var now = DateTimeOffset.UtcNow;
        var first = new StoryBibleEntry(Guid.NewGuid(), "fact", "First", ["Content"], [], 3, 0);
        var second = new StoryBibleEntry(Guid.NewGuid(), "fact", "Second", ["Content"], [], 3, 0);
        var definition = new StoryDefinition(
            Guid.NewGuid(), "Story", "A sufficiently long prompt for validation.", new([first, second]), [], 0, now, now);
        var storyGeneration = NarratorDefaults.Create().StoryGeneration with { MaxStoryBibleEntries = 1 };

        Assert.Throws<InvalidDataException>(() => ImportExportProcessor.CopyDefinition(
            definition, 1, NarratorDefaults.Create().ContentLimits, storyGeneration));
    }

    [Fact]
    public void StateCopy_RejectsNonContiguousTurnSequence()
    {
        var (state, opening) = CreateState();
        var second = opening with { Id = Guid.NewGuid(), SequenceNumber = 2, PlayerAction = "Continue" };
        var withLatestSequence = state with { LastCommittedTurnSequence = 2 };

        Assert.Throws<InvalidDataException>(() => ImportExportProcessor.CopyState(
            withLatestSequence, [opening, second], 1, NarratorDefaults.Create().ContentLimits, NarratorDefaults.Create().StoryGeneration));
    }

    [Fact]
    public void StateCopy_RejectsDuplicateTurnIds()
    {
        var (state, opening) = CreateState();
        var second = opening with { SequenceNumber = 1, PlayerAction = "Continue" };
        var withLatestSequence = state with { LastCommittedTurnSequence = 1 };

        Assert.Throws<InvalidDataException>(() => ImportExportProcessor.CopyState(
            withLatestSequence, [opening, second], 1, NarratorDefaults.Create().ContentLimits, NarratorDefaults.Create().StoryGeneration));
    }

    [Fact]
    public void StateCopy_RejectsTurnWithEmptyId()
    {
        var (state, opening) = CreateState();
        var withEmptyId = opening with { Id = Guid.Empty };

        Assert.Throws<InvalidDataException>(() => ImportExportProcessor.CopyState(
            state, [withEmptyId], 1, NarratorDefaults.Create().ContentLimits, NarratorDefaults.Create().StoryGeneration));
    }

    [Fact]
    public void StateCopy_RejectsTurnWithMismatchedStoryStateId()
    {
        var (state, opening) = CreateState();
        var mismatched = opening with { StoryStateId = Guid.NewGuid() };

        Assert.Throws<InvalidDataException>(() => ImportExportProcessor.CopyState(
            state, [mismatched], 1, NarratorDefaults.Create().ContentLimits, NarratorDefaults.Create().StoryGeneration));
    }

    [Fact]
    public void StateCopy_RejectsOpeningTurnWithPlayerAction()
    {
        var (state, opening) = CreateState();
        var withAction = opening with { PlayerAction = "Not allowed on the opening turn" };

        Assert.Throws<InvalidDataException>(() => ImportExportProcessor.CopyState(
            state, [withAction], 1, NarratorDefaults.Create().ContentLimits, NarratorDefaults.Create().StoryGeneration));
    }

    [Fact]
    public void StateCopy_RejectsChangeWithInvalidBeforeAfterShape()
    {
        var (state, opening) = CreateState();
        var entry = state.CurrentStoryBible.Entries[0];
        // An Add change must have Before = null; supplying one makes the shape inconsistent.
        var invalidChange = new AppliedStoryBibleChange(StoryBibleOperation.Add, entry.Id, entry, entry, StoryBibleChangeSource.LlmUpdate);
        var withChange = opening with { StoryBibleChanges = [invalidChange] };

        Assert.Throws<InvalidDataException>(() => ImportExportProcessor.CopyState(
            state, [withChange], 1, NarratorDefaults.Create().ContentLimits, NarratorDefaults.Create().StoryGeneration));
    }

    [Fact]
    public void StateCopy_RejectsBibleExceedingStoryGenerationCharacterLimit()
    {
        var (state, opening) = CreateState();
        var storyGeneration = NarratorDefaults.Create().StoryGeneration with { MaxStoryBibleEntryCharacters = 1 };

        Assert.Throws<InvalidDataException>(() => ImportExportProcessor.CopyState(
            state, [opening], 1, NarratorDefaults.Create().ContentLimits, storyGeneration));
    }

    [Fact]
    public async Task ReadLimitedAsync_RejectsStreamExceedingMaximumImportBytes()
    {
        using var stream = new MemoryStream(new byte[ImportExportProcessor.MaximumImportBytes + 1]);

        await Assert.ThrowsAsync<InvalidDataException>(() => ImportExportProcessor.ReadLimitedAsync(stream));
    }

    [Fact]
    public async Task ReadLimitedAsync_RejectsNonSeekableStreamExceedingMaximumImportBytesWhileReading()
    {
        await using var stream = new NonSeekableStream(new byte[ImportExportProcessor.MaximumImportBytes + 1]);

        await Assert.ThrowsAsync<InvalidDataException>(() => ImportExportProcessor.ReadLimitedAsync(stream));
    }

    [Fact]
    public async Task ReadLimitedAsync_ReturnsExactBytesForAStreamWithinTheLimit()
    {
        var bytes = new byte[] { 1, 2, 3, 4, 5 };
        using var stream = new MemoryStream(bytes);

        var result = await ImportExportProcessor.ReadLimitedAsync(stream);

        Assert.Equal(bytes, result);
    }

    private sealed class NonSeekableStream(byte[] data) : Stream
    {
        private readonly MemoryStream _inner = new(data);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _inner.Position; set => throw new NotSupportedException(); }
        public override void Flush() => _inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            _inner.ReadAsync(buffer, offset, count, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing) _inner.Dispose();
            base.Dispose(disposing);
        }
    }

    private static (StoryState State, StoryTurn Turn) CreateState()
    {
        var stateId = Guid.NewGuid();
        var entry = new StoryBibleEntry(Guid.NewGuid(), "fact", "Name", ["The player is Alex."], [], 4, 0);
        var bible = new StoryBible([entry]);
        var snapshot = new StoryDefinitionSnapshot(
            "Story",
            "A sufficiently long prompt for validation.",
            bible);
        var now = DateTimeOffset.UtcNow;
        var state = new StoryState(stateId, "Story", null, new(snapshot), bible, [], 0, now, null, 0);
        var turn = new StoryTurn(
            Guid.NewGuid(),
            stateId,
            0,
            null,
            "Opening",
            ["Continue"],
            [entry.Id],
            [],
            now,
            new("model", null, null, null));
        return (state, turn);
    }
}
