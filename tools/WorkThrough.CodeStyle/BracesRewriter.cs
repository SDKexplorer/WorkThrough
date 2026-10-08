using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

internal sealed class BracesRewriter : CSharpSyntaxRewriter
{
    private static StatementSyntax Wrap(StatementSyntax statement) => statement is BlockSyntax ? statement :
        SyntaxFactory.Block(statement.WithoutLeadingTrivia()).WithLeadingTrivia(statement.GetLeadingTrivia());
    public override SyntaxNode? VisitIfStatement(IfStatementSyntax node) => base.VisitIfStatement(node.WithStatement(Wrap(node.Statement)));
    public override SyntaxNode? VisitElseClause(ElseClauseSyntax node) => base.VisitElseClause(node.Statement is IfStatementSyntax ? node : node.WithStatement(Wrap(node.Statement)));
    public override SyntaxNode? VisitForStatement(ForStatementSyntax node) => base.VisitForStatement(node.WithStatement(Wrap(node.Statement)));
    public override SyntaxNode? VisitForEachStatement(ForEachStatementSyntax node) => base.VisitForEachStatement(node.WithStatement(Wrap(node.Statement)));
    public override SyntaxNode? VisitWhileStatement(WhileStatementSyntax node) => base.VisitWhileStatement(node.WithStatement(Wrap(node.Statement)));
    public override SyntaxNode? VisitDoStatement(DoStatementSyntax node) => base.VisitDoStatement(node.WithStatement(Wrap(node.Statement)));
    public override SyntaxNode? VisitLockStatement(LockStatementSyntax node) => base.VisitLockStatement(node.WithStatement(Wrap(node.Statement)));
    public override SyntaxNode? VisitUsingStatement(UsingStatementSyntax node) => base.VisitUsingStatement(node.WithStatement(Wrap(node.Statement)));
    public override SyntaxNode? VisitBlock(BlockSyntax node)
    {
        node = (BlockSyntax)base.VisitBlock(node)!;
        var open = node.OpenBraceToken;
        var close = node.CloseBraceToken;
        if (!open.TrailingTrivia.Any(t => t.IsKind(SyntaxKind.EndOfLineTrivia)))
        {
            open = open.WithTrailingTrivia(open.TrailingTrivia.Add(SyntaxFactory.EndOfLine("\n")));
        }

        // Do not leave a decorative blank line before the end of a scope.
        if (close.LeadingTrivia.All(t => t.IsKind(SyntaxKind.WhitespaceTrivia) || t.IsKind(SyntaxKind.EndOfLineTrivia)))
        {
            var lastNewline = -1;
            for (var index = 0; index < close.LeadingTrivia.Count; index++)
            {
                if (close.LeadingTrivia[index].IsKind(SyntaxKind.EndOfLineTrivia))
                {
                    lastNewline = index;
                }
            }

            if (lastNewline >= 0)
            {
                close = close.WithLeadingTrivia(SyntaxFactory.TriviaList(close.LeadingTrivia.Skip(lastNewline + 1)));
            }
        }

        var statements = node.Statements.Select(statement =>
            statement.GetTrailingTrivia().Any(t => t.IsKind(SyntaxKind.EndOfLineTrivia)) ? statement :
            statement.WithTrailingTrivia(statement.GetTrailingTrivia().Add(SyntaxFactory.EndOfLine("\n"))));

        return node.WithOpenBraceToken(open).WithCloseBraceToken(close).WithStatements(SyntaxFactory.List(statements));
    }
    public override SyntaxNode? VisitPropertyDeclaration(PropertyDeclarationSyntax node)
    {
        node = (PropertyDeclarationSyntax)base.VisitPropertyDeclaration(node)!;
        if (node.AccessorList is { } accessors && accessors.Accessors.All(a => a.Body is null && a.ExpressionBody is null)
            && accessors.GetLeadingTrivia().All(t => t.IsKind(SyntaxKind.WhitespaceTrivia) || t.IsKind(SyntaxKind.EndOfLineTrivia))
            && !node.Identifier.TrailingTrivia.Any(t => !t.IsKind(SyntaxKind.WhitespaceTrivia) && !t.IsKind(SyntaxKind.EndOfLineTrivia)))
        {
            return node.WithIdentifier(node.Identifier.WithTrailingTrivia(SyntaxFactory.Space))
                .WithAccessorList(accessors.WithLeadingTrivia(SyntaxFactory.TriviaList()));
        }

        return node;
    }

    public override SyntaxNode? VisitAccessorList(AccessorListSyntax node)
    {
        if (node.Accessors.All(a => a.Body is null && a.ExpressionBody is null) &&
            !node.DescendantTrivia().Any(t => t.IsKind(SyntaxKind.SingleLineCommentTrivia) || t.IsKind(SyntaxKind.MultiLineCommentTrivia)))
        {
            return SyntaxFactory.ParseCompilationUnit("class C { int P { " + string.Join(" ", node.Accessors.Select(a => a.ToString().Trim())) + " } }")
                .DescendantNodes().OfType<AccessorListSyntax>().Single().WithTriviaFrom(node);
        }

        return base.VisitAccessorList(node);
    }
}
