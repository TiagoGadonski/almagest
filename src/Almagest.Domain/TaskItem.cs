namespace Almagest.Domain;

// Named TaskItem, not Task -- System.Threading.Tasks.Task is in scope via
// ImplicitUsings in every project here, and would collide with every async
// method signature in the codebase (same reasoning for TaskItemStatus vs.
// the BCL's System.Threading.Tasks.TaskStatus).
public enum TaskItemStatus
{
    Open,
    InProgress,
    Done,
    Cancelled,
}

public sealed class TaskItem
{
    public Guid Id { get; }

    public Guid? ProjectId { get; }

    public Guid? SourceDocumentId { get; }

    public string Title { get; }

    public TaskItemStatus Status { get; }

    public DateOnly? DueDate { get; }

    public DateTimeOffset CreatedAt { get; }

    private TaskItem(
        Guid id,
        Guid? projectId,
        Guid? sourceDocumentId,
        string title,
        TaskItemStatus status,
        DateOnly? dueDate,
        DateTimeOffset createdAt)
    {
        Id = id;
        ProjectId = projectId;
        SourceDocumentId = sourceDocumentId;
        Title = title;
        Status = status;
        DueDate = dueDate;
        CreatedAt = createdAt;
    }

    public static TaskItem Create(
        string title,
        Guid? projectId = null,
        Guid? sourceDocumentId = null,
        TaskItemStatus status = TaskItemStatus.Open,
        DateOnly? dueDate = null)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            throw new ArgumentException("Task title cannot be empty.", nameof(title));
        }

        return new TaskItem(Guid.NewGuid(), projectId, sourceDocumentId, title, status, dueDate, DateTimeOffset.UtcNow);
    }

    internal static TaskItem Reconstitute(
        Guid id,
        Guid? projectId,
        Guid? sourceDocumentId,
        string title,
        TaskItemStatus status,
        DateOnly? dueDate,
        DateTimeOffset createdAt)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Persisted task must have an id.", nameof(id));
        }

        return new TaskItem(id, projectId, sourceDocumentId, title, status, dueDate, createdAt);
    }
}
