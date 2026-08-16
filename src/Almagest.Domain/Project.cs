namespace Almagest.Domain;

public enum ProjectStatus
{
    Active,
    Completed,
    Archived,
}

public sealed class Project
{
    public Guid Id { get; }

    public string Name { get; }

    public ProjectStatus Status { get; }

    public DateTimeOffset CreatedAt { get; }

    private Project(Guid id, string name, ProjectStatus status, DateTimeOffset createdAt)
    {
        Id = id;
        Name = name;
        Status = status;
        CreatedAt = createdAt;
    }

    public static Project Create(string name, ProjectStatus status = ProjectStatus.Active)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Project name cannot be empty.", nameof(name));
        }

        return new Project(Guid.NewGuid(), name, status, DateTimeOffset.UtcNow);
    }

    internal static Project Reconstitute(Guid id, string name, ProjectStatus status, DateTimeOffset createdAt)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Persisted project must have an id.", nameof(id));
        }

        return new Project(id, name, status, createdAt);
    }
}
