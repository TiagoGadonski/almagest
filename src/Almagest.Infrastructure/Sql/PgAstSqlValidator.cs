using Almagest.Application.Ports;
using PgSqlParser;

namespace Almagest.Infrastructure.Sql;

// Layer 3 of the phase doc's security design (3.4, checks enumerated in
// 3.6): every generated query is parsed with the actual PostgreSQL grammar
// (libpg_query, via pgsqlparser) and its syntax tree is walked -- not
// pattern-matched -- before it is treated as safe to execute. Every check
// here is independent of Layer 4 (the database role's own grants): this
// validator assumes the role's grants could be misconfigured, and the role
// assumes this validator could have a bug.
public sealed class PgAstSqlValidator : ISqlValidator
{
    private static readonly HashSet<string> AllowedFunctions = new(StringComparer.OrdinalIgnoreCase)
    {
        "count", "sum", "avg", "min", "max",
        "lower", "upper", "coalesce", "now", "date_trunc", "extract", "age", "to_char", "concat",
    };

    private readonly SqlAllowlist _allowlist;
    private readonly int _maxRows;

    public PgAstSqlValidator(SqlAllowlist allowlist, int maxRows)
    {
        _allowlist = allowlist;
        _maxRows = maxRows;
    }

    public SqlValidationResult Validate(string sql)
    {
        var scanResult = Parser.Scan(sql);
        if (!scanResult.IsSuccess)
        {
            return Reject($"Query failed to tokenize: {scanResult.Error!.Message}");
        }

        // Comments are discarded during parsing, so their absence from the
        // AST proves nothing -- checked here, against the token stream,
        // before the query is treated as SQL at all.
        if (scanResult.Value!.Tokens.Any(token => token.Token == Token.SqlComment))
        {
            return Reject("Comments are not allowed in generated queries.");
        }

        var splitResult = Parser.SplitWithParser(sql);
        if (!splitResult.IsSuccess)
        {
            return Reject($"Query failed to parse: {splitResult.Error!.Message}");
        }

        if (splitResult.Value!.Statements.Count != 1)
        {
            return Reject("Only a single statement is allowed.");
        }

        var parseResult = Parser.Parse(sql, new ParserOptions());
        if (!parseResult.IsSuccess)
        {
            return Reject($"Query failed to parse: {parseResult.Error!.Message}");
        }

        if (parseResult.Value!.Stmts.Count != 1)
        {
            return Reject("Only a single statement is allowed.");
        }

        var topLevel = parseResult.Value.Stmts[0].Stmt;
        if (topLevel.SelectStmt is not { } select)
        {
            return Reject($"Only SELECT statements are allowed (found {topLevel.NodeCase}).");
        }

        var validationError = ValidateSelect(select);
        if (validationError is not null)
        {
            return Reject(validationError);
        }

        var (finalizedSql, limitError) = FinalizeLimit(sql, select);
        return limitError is not null
            ? Reject(limitError)
            : new SqlValidationResult(true, finalizedSql, null);
    }

    private string? ValidateSelect(SelectStmt select)
    {
        // PostgreSQL allows data-modifying statements inside a WITH clause
        // (WITH x AS (DELETE FROM ... RETURNING ...) SELECT * FROM x) -- a
        // SELECT-shaped top level can still smuggle a write. Recursive on
        // purpose: every CTE, including nested ones, must itself be
        // SELECT-only.
        if (select.WithClause is not null)
        {
            foreach (var cteNode in select.WithClause.Ctes)
            {
                if (cteNode.CommonTableExpr is not { } cte)
                {
                    return "Unsupported construct in WITH clause.";
                }

                if (cte.Ctequery?.SelectStmt is not { } cteSelect)
                {
                    return $"Common table expression '{cte.Ctename}' must be a SELECT.";
                }

                var cteError = ValidateSelect(cteSelect);
                if (cteError is not null)
                {
                    return cteError;
                }
            }
        }

        // First pass: check every FROM-clause table against the allowlist
        // and record alias -> real-table-name (a JOIN's ON condition can
        // reference an alias introduced by a sibling from-item, so the map
        // has to be complete before any qualified column is resolved).
        var aliasToTable = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var fromNode in select.FromClause)
        {
            var error = CollectFromItem(fromNode, aliasToTable);
            if (error is not null)
            {
                return error;
            }
        }

        // Second pass: validate join conditions now that every alias is known.
        foreach (var fromNode in select.FromClause)
        {
            var error = ValidateJoinQuals(fromNode, aliasToTable);
            if (error is not null)
            {
                return error;
            }
        }

        foreach (var targetNode in select.TargetList)
        {
            var error = ValidateNode(targetNode, aliasToTable);
            if (error is not null)
            {
                return error;
            }
        }

        if (select.WhereClause is not null)
        {
            var error = ValidateNode(select.WhereClause, aliasToTable);
            if (error is not null)
            {
                return error;
            }
        }

        foreach (var groupNode in select.GroupClause)
        {
            var error = ValidateNode(groupNode, aliasToTable);
            if (error is not null)
            {
                return error;
            }
        }

        foreach (var sortNode in select.SortClause)
        {
            var error = ValidateNode(sortNode, aliasToTable);
            if (error is not null)
            {
                return error;
            }
        }

        return null;
    }

    private string? CollectFromItem(Node node, Dictionary<string, string> aliasToTable) => node.NodeCase switch
    {
        Node.NodeOneofCase.RangeVar => CollectRangeVar(node.RangeVar, aliasToTable),
        Node.NodeOneofCase.JoinExpr => CollectFromItem(node.JoinExpr.Larg, aliasToTable)
            ?? CollectFromItem(node.JoinExpr.Rarg, aliasToTable),
        _ => $"Unsupported FROM-clause construct: {node.NodeCase}.",
    };

    private string? CollectRangeVar(RangeVar rangeVar, Dictionary<string, string> aliasToTable)
    {
        if (!_allowlist.IsTableAllowed(rangeVar.Relname))
        {
            return $"Table '{rangeVar.Relname}' is not on the allowlist.";
        }

        var alias = rangeVar.Alias?.Aliasname;
        if (alias is { Length: > 0 })
        {
            aliasToTable[alias] = rangeVar.Relname;
        }

        aliasToTable[rangeVar.Relname] = rangeVar.Relname; // the bare table name always resolves to itself
        return null;
    }

    private string? ValidateJoinQuals(Node node, Dictionary<string, string> aliasToTable)
    {
        if (node.NodeCase != Node.NodeOneofCase.JoinExpr)
        {
            return null;
        }

        var join = node.JoinExpr;

        var leftError = ValidateJoinQuals(join.Larg, aliasToTable);
        if (leftError is not null)
        {
            return leftError;
        }

        var rightError = ValidateJoinQuals(join.Rarg, aliasToTable);
        if (rightError is not null)
        {
            return rightError;
        }

        return join.Quals is not null ? ValidateNode(join.Quals, aliasToTable) : null;
    }

    private string? ValidateNode(Node node, Dictionary<string, string> aliasToTable)
    {
        switch (node.NodeCase)
        {
            case Node.NodeOneofCase.ColumnRef:
                return ValidateColumnRef(node.ColumnRef, aliasToTable);
            case Node.NodeOneofCase.FuncCall:
                return ValidateFuncCall(node.FuncCall, aliasToTable);
            case Node.NodeOneofCase.ResTarget:
                return node.ResTarget.Val is not null ? ValidateNode(node.ResTarget.Val, aliasToTable) : null;
            case Node.NodeOneofCase.SortBy:
                return node.SortBy.Node is not null ? ValidateNode(node.SortBy.Node, aliasToTable) : null;
            case Node.NodeOneofCase.AExpr:
                var leftError = node.AExpr.Lexpr is not null ? ValidateNode(node.AExpr.Lexpr, aliasToTable) : null;
                return leftError ?? (node.AExpr.Rexpr is not null ? ValidateNode(node.AExpr.Rexpr, aliasToTable) : null);
            case Node.NodeOneofCase.BoolExpr:
                return node.BoolExpr.Args.Select(arg => ValidateNode(arg, aliasToTable)).FirstOrDefault(error => error is not null);
            case Node.NodeOneofCase.SubLink:
                return node.SubLink.Subselect?.SelectStmt is { } subSelect
                    ? ValidateSelect(subSelect)
                    : "Unsupported subquery construct.";
            case Node.NodeOneofCase.AConst:
            case Node.NodeOneofCase.TypeCast:
                // Literals and type casts carry no table/column/function reference to check.
                return null;
            default:
                return null;
        }
    }

    private string? ValidateColumnRef(ColumnRef columnRef, Dictionary<string, string> aliasToTable)
    {
        var parts = columnRef.Fields
            .Where(field => field.NodeCase == Node.NodeOneofCase.String)
            .Select(field => field.String.Sval)
            .ToList();

        if (parts.Count == 0)
        {
            return null; // "*"
        }

        if (parts.Count >= 2)
        {
            var qualifier = parts[^2];
            var column = parts[^1];
            var table = aliasToTable.GetValueOrDefault(qualifier, qualifier);
            return _allowlist.IsColumnAllowed(table, column) ? null : $"Column '{qualifier}.{column}' is not on the allowlist.";
        }

        // Unqualified reference in a (possibly multi-table) query: falls
        // back to "is this column name allowlisted for any referenced
        // table" rather than resolving which table it actually binds to --
        // full binding resolution is semantic analysis, not tree-walking.
        // Documented as a known limitation (phase doc 3.6, item 6).
        var unqualifiedColumn = parts[0];
        return _allowlist.IsColumnAllowedForAnyTable(unqualifiedColumn)
            ? null
            : $"Column '{unqualifiedColumn}' is not on the allowlist.";
    }

    private string? ValidateFuncCall(FuncCall funcCall, Dictionary<string, string> aliasToTable)
    {
        var name = funcCall.Funcname
            .Where(field => field.NodeCase == Node.NodeOneofCase.String)
            .Select(field => field.String.Sval)
            .LastOrDefault();

        if (name is null || !AllowedFunctions.Contains(name))
        {
            return $"Function '{name ?? "?"}' is not on the allowlist.";
        }

        return funcCall.Args.Select(arg => ValidateNode(arg, aliasToTable)).FirstOrDefault(error => error is not null);
    }

    private (string? Sql, string? Error) FinalizeLimit(string sql, SelectStmt select)
    {
        var trimmed = sql.TrimEnd().TrimEnd(';');

        if (select.LimitCount is null)
        {
            return ($"{trimmed} LIMIT {_maxRows}", null);
        }

        var literalLimit = select.LimitCount.AConst?.Ival?.Ival;
        if (literalLimit is null)
        {
            return (null, "LIMIT must be a literal integer.");
        }

        return literalLimit.Value > _maxRows
            ? ($"SELECT * FROM ({trimmed}) AS capped LIMIT {_maxRows}", null)
            : (trimmed, null);
    }

    private static SqlValidationResult Reject(string reason) => new(false, null, reason);
}
