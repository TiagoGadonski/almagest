using Almagest.Domain;

namespace Almagest.Application.Ports;

public interface INoteStore
{
    Task SaveAsync(Note note, CancellationToken cancellationToken = default);
}
