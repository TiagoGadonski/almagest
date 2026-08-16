using Almagest.Application.Ports;
using Almagest.Domain;

namespace Almagest.UnitTests.TestDoubles;

public sealed class FakeNoteStore : INoteStore
{
    public List<Note> Saved { get; } = [];

    public Task SaveAsync(Note note, CancellationToken cancellationToken = default)
    {
        Saved.Add(note);
        return Task.CompletedTask;
    }
}
