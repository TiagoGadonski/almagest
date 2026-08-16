using Almagest.Application.Ports;
using Almagest.Domain;

namespace Almagest.Application.UseCases;

public sealed record CreateNoteResult(Guid NoteId);

public sealed class CreateNoteUseCase
{
    private readonly INoteStore _noteStore;

    public CreateNoteUseCase(INoteStore noteStore)
    {
        _noteStore = noteStore;
    }

    public async Task<CreateNoteResult> ExecuteAsync(string content, CancellationToken cancellationToken = default)
    {
        var note = Note.Create(content);
        await _noteStore.SaveAsync(note, cancellationToken).ConfigureAwait(false);
        return new CreateNoteResult(note.Id);
    }
}
