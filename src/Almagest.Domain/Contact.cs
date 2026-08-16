namespace Almagest.Domain;

public sealed class Contact
{
    public Guid Id { get; }

    public string Name { get; }

    public string? Email { get; }

    public string? Phone { get; }

    public DateTimeOffset CreatedAt { get; }

    private Contact(Guid id, string name, string? email, string? phone, DateTimeOffset createdAt)
    {
        Id = id;
        Name = name;
        Email = email;
        Phone = phone;
        CreatedAt = createdAt;
    }

    public static Contact Create(string name, string? email = null, string? phone = null)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Contact name cannot be empty.", nameof(name));
        }

        return new Contact(Guid.NewGuid(), name, email, phone, DateTimeOffset.UtcNow);
    }

    internal static Contact Reconstitute(Guid id, string name, string? email, string? phone, DateTimeOffset createdAt)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Persisted contact must have an id.", nameof(id));
        }

        return new Contact(id, name, email, phone, createdAt);
    }
}
