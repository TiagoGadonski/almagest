using Almagest.Infrastructure.Sql;

namespace Almagest.UnitTests.Sql;

// The security test suite the request specifically asked for as a
// first-class deliverable: injection and malicious-query attempts, not just
// the happy path. Runs the real PgAstSqlValidator (pure computation, no
// fakes needed) against actual attack shapes.
public class PgAstSqlValidatorTests
{
    private static readonly PgAstSqlValidator Validator = new(SqlAllowlist.Default, maxRows: 200);

    [Fact]
    public void Validate_SimpleAllowlistedQueryWithLimit_IsAccepted()
    {
        var result = Validator.Validate("SELECT id, name FROM contacts WHERE name = 'Alice' LIMIT 10");

        Assert.True(result.IsValid);
        Assert.NotNull(result.FinalizedSql);
        Assert.Contains("LIMIT 10", result.FinalizedSql);
    }

    [Fact]
    public void Validate_MultiStatementInjection_IsRejected()
    {
        var result = Validator.Validate("SELECT 1; DROP TABLE contacts;");

        Assert.False(result.IsValid);
        Assert.Contains("single statement", result.RejectionReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_CommentSmuggling_IsRejected()
    {
        var result = Validator.Validate("SELECT id FROM contacts -- ; DROP TABLE contacts\nLIMIT 5");

        Assert.False(result.IsValid);
        Assert.Contains("comment", result.RejectionReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_DataModifyingCteDisguisedAsSelect_IsRejected()
    {
        var result = Validator.Validate("WITH x AS (DELETE FROM contacts RETURNING id) SELECT * FROM x");

        Assert.False(result.IsValid);
        Assert.Contains("SELECT", result.RejectionReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_NestedDataModifyingCte_IsRejected()
    {
        var result = Validator.Validate(
            "WITH outer_cte AS (WITH inner_cte AS (UPDATE contacts SET name = 'x' RETURNING id) SELECT * FROM inner_cte) SELECT * FROM outer_cte");

        Assert.False(result.IsValid);
    }

    [Fact]
    public void Validate_OffAllowlistTable_IsRejected()
    {
        var result = Validator.Validate("SELECT id FROM sessions LIMIT 5");

        Assert.False(result.IsValid);
        Assert.Contains("sessions", result.RejectionReason);
        Assert.Contains("allowlist", result.RejectionReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_OffAllowlistColumn_IsRejected()
    {
        var result = Validator.Validate("SELECT id, ssn FROM contacts LIMIT 5");

        Assert.False(result.IsValid);
        Assert.Contains("ssn", result.RejectionReason);
    }

    [Fact]
    public void Validate_DisallowedFunctionCall_IsRejected()
    {
        var result = Validator.Validate("SELECT pg_sleep(10)");

        Assert.False(result.IsValid);
        Assert.Contains("pg_sleep", result.RejectionReason);
    }

    [Fact]
    public void Validate_AllowlistedAggregateFunction_IsAccepted()
    {
        var result = Validator.Validate("SELECT status, count(*) FROM tasks GROUP BY status LIMIT 20");

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Validate_MissingLimit_AppendsConfiguredMax()
    {
        var result = Validator.Validate("SELECT id FROM contacts");

        Assert.True(result.IsValid);
        Assert.Contains("LIMIT 200", result.FinalizedSql);
    }

    [Fact]
    public void Validate_OversizedLimit_IsCappedToConfiguredMax()
    {
        var result = Validator.Validate("SELECT id FROM contacts LIMIT 999999");

        // The inner LIMIT 999999 is wrapped, not string-edited -- the actual
        // security property is that an outer LIMIT 200 bounds how many rows
        // ever come back, regardless of what the inner clause says.
        Assert.True(result.IsValid);
        Assert.EndsWith("LIMIT 200", result.FinalizedSql);
    }

    [Theory]
    [InlineData("DROP TABLE contacts")]
    [InlineData("INSERT INTO contacts (name) VALUES ('x')")]
    [InlineData("UPDATE contacts SET name = 'x'")]
    [InlineData("DELETE FROM contacts")]
    [InlineData("TRUNCATE contacts")]
    [InlineData("ALTER TABLE contacts ADD COLUMN evil TEXT")]
    [InlineData("CREATE TABLE evil (id int)")]
    public void Validate_NonSelectTopLevelStatement_IsRejected(string sql)
    {
        var result = Validator.Validate(sql);

        Assert.False(result.IsValid);
    }

    [Fact]
    public void Validate_JoinAcrossAllowlistedTables_IsAccepted()
    {
        var result = Validator.Validate("SELECT t.title, p.name FROM tasks t JOIN projects p ON p.id = t.project_id LIMIT 20");

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Validate_JoinIncludingOffAllowlistTable_IsRejected()
    {
        var result = Validator.Validate("SELECT t.title FROM tasks t JOIN messages m ON m.id = t.id LIMIT 20");

        Assert.False(result.IsValid);
    }

    [Fact]
    public void Validate_UnparseableQuery_IsRejected()
    {
        var result = Validator.Validate("SELEC FROM garbage !!!");

        Assert.False(result.IsValid);
    }

    [Fact]
    public void Validate_EmptyQuery_IsRejected()
    {
        var result = Validator.Validate(string.Empty);

        Assert.False(result.IsValid);
    }
}
