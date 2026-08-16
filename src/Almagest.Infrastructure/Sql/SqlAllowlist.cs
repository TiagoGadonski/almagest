namespace Almagest.Infrastructure.Sql;

// The single source of truth for which tables/columns text-to-SQL may touch
// -- shared by PostgresSchemaProvider (what the model is shown) and
// PgAstSqlValidator (what's checked after generation). Must stay in sync
// with the GRANT statements in db/migrations/0003_text_to_sql.sql -- that
// migration is the independent, database-enforced copy of this same list
// (phase doc 3.4, Layer 2/4).
public sealed record SqlAllowlist(IReadOnlyDictionary<string, IReadOnlyList<string>> TableColumns)
{
    public bool IsTableAllowed(string tableName) => TableColumns.ContainsKey(tableName);

    public bool IsColumnAllowed(string tableName, string columnName) =>
        TableColumns.TryGetValue(tableName, out var columns) && columns.Contains(columnName);

    public bool IsColumnAllowedForAnyTable(string columnName) =>
        TableColumns.Values.Any(columns => columns.Contains(columnName));

    public static SqlAllowlist Default { get; } = new(new Dictionary<string, IReadOnlyList<string>>
    {
        ["contacts"] = ["id", "name", "email", "phone", "created_at"],
        ["projects"] = ["id", "name", "status", "created_at"],
        ["tasks"] = ["id", "project_id", "source_document_id", "title", "status", "due_date", "created_at"],
        ["calendar_events"] = ["id", "title", "starts_at", "ends_at", "location", "related_contact_id", "created_at"],
        ["documents"] = ["id", "title", "document_type", "document_date_start", "document_date_end", "extracted_metadata", "created_at"],
    });
}
