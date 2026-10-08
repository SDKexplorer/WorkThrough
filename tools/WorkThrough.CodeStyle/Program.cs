using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

var braces = args.Contains("--braces", StringComparer.Ordinal);
var check = args.Contains("--check", StringComparer.Ordinal);
var root = Path.GetFullPath(args.LastOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal)) ?? ".");
var changed = new List<string>();
var excluded = new HashSet<string>(StringComparer.Ordinal) { "bin", "obj", "artifacts", "node_modules", ".git", ".angular" };

foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
{
    var relative = Path.GetRelativePath(root, file);
    if (relative.Split(Path.DirectorySeparatorChar).Any(excluded.Contains))
    {
        continue;
    }

    var content = await File.ReadAllTextAsync(file);
    var tree = CSharpSyntaxTree.ParseText(content, new CSharpParseOptions(LanguageVersion.Preview));
    if (tree.GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error))
    {
        throw new InvalidOperationException($"Cannot format syntactically invalid C#: {relative}\n{string.Join("\n", tree.GetDiagnostics())}");
    }

    if (braces)
    {
        var rewritten = (CompilationUnitSyntax)new BracesRewriter().Visit(tree.GetRoot())!;
        var updated = rewritten.ToFullString();
        if (updated != content)
        {
            changed.Add(relative);
            if (!check)
            {
                await File.WriteAllTextAsync(file, updated);
            }
        }

        continue;
    }

    var text = tree.GetText();
    var insertions = new SortedSet<int>();
    foreach (var block in tree.GetRoot().DescendantNodes().OfType<BlockSyntax>())
    {
        for (var index = 1; index < block.Statements.Count; index++)
        {
            var current = block.Statements[index];
            var previous = block.Statements[index - 1];
            if (current is ReturnStatementSyntax || EndsBlock(previous))
            {
                Separate(current, previous);
            }
        }
    }

    var globals = tree.GetRoot().ChildNodes().OfType<GlobalStatementSyntax>().ToArray();
    for (var index = 1; index < globals.Length; index++)
    {
        if (globals[index].Statement is ReturnStatementSyntax || EndsBlock(globals[index - 1].Statement))
        {
            Separate(globals[index].Statement, globals[index - 1].Statement);
        }
    }

    if (insertions.Count == 0)
    {
        continue;
    }

    changed.Add(relative);
    if (!check)
    {
        var updated = text.WithChanges(insertions.Select(position => new TextChange(new TextSpan(position, 0), "\n")));
        await File.WriteAllTextAsync(file, updated.ToString());
    }

    void Separate(StatementSyntax current, StatementSyntax previous)
    {
        var first = current.GetFirstToken();
        var comments = first.LeadingTrivia.Where(t => !t.IsKind(SyntaxKind.WhitespaceTrivia) && !t.IsKind(SyntaxKind.EndOfLineTrivia)).ToArray();
        var start = comments.Length == 0 ? current.SpanStart : comments[0].SpanStart;
        var line = text.Lines.GetLineFromPosition(start);
        var previousLine = text.Lines.GetLineFromPosition(previous.Span.End).LineNumber;
        if (line.LineNumber > previousLine && line.LineNumber > 0 && !string.IsNullOrWhiteSpace(text.Lines[line.LineNumber - 1].ToString()))
        {
            insertions.Add(line.Start);
        }
    }
}

foreach (var file in changed)
{
    Console.WriteLine($"{(check ? "Needs spacing" : "Formatted")}: {file}");
}

Console.WriteLine($"Spacing {(check ? "check" : "format")}: {changed.Count} file(s).");

return check && changed.Count > 0 ? 1 : 0;

static bool EndsBlock(StatementSyntax statement) => statement.GetLastToken().IsKind(SyntaxKind.CloseBraceToken)
    || statement is DoStatementSyntax { Statement: BlockSyntax };
