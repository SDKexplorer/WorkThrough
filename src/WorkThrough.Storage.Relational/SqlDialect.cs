namespace WorkThrough.Storage;

public abstract partial class RelationalJobStore
{
    // These fragments receive library-owned SQL only, never caller-supplied identifiers or expressions.
    protected virtual string PageSql(string limit, string offset = "0") => $"LIMIT {limit} OFFSET {offset}";

    protected string PageSql(int limit) => PageSql(limit.ToString(System.Globalization.CultureInfo.InvariantCulture));

    /// <summary>Bounds operator-visible labels to <see cref="OperationQueryLimits.MaximumLabelLength"/> characters.</summary>
    private string ClippedLabelSql(string expression) => $"{SubstringFunction}({expression},1,{OperationQueryLimits.MaximumLabelLength})";

    private string LabelTooLongSql(string expression) => $"{LengthSql(expression)}>{OperationQueryLimits.MaximumLabelLength}";
    protected virtual string LengthSql(string expression) => $"LENGTH({expression})";
    protected virtual string SubstringFunction => "SUBSTR";
    protected virtual string TextType => "TEXT";
    protected virtual string FloatingPointType => "DOUBLE PRECISION";
    protected virtual string AttemptNumberSql => "number";
    protected virtual string DiagnosticExceptionSql => "COALESCE(f.exception_type,'Unspecified')";

    /// <summary>Composes a single-row upsert inside the provider's serialized write transaction.</summary>
    protected virtual string UpsertSql(string table, string columns, string values, string keys, string updates = "")
    {
        var conflict = updates.Length == 0 ? "DO NOTHING" : "DO UPDATE SET " +
            string.Join(",", updates.Split(',').Select(column => $"{column}=excluded.{column}"));

        return $"INSERT INTO {table}({columns}) VALUES ({values}) ON CONFLICT({keys}) {conflict}";
    }
}
