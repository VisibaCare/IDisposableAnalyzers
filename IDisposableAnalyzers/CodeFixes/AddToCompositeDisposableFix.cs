﻿namespace IDisposableAnalyzers
{
    using System.Collections.Immutable;
    using System.Composition;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using Gu.Roslyn.AnalyzerExtensions;
    using Gu.Roslyn.CodeFixExtensions;
    using Microsoft.CodeAnalysis;
    using Microsoft.CodeAnalysis.CodeFixes;
    using Microsoft.CodeAnalysis.CSharp;
    using Microsoft.CodeAnalysis.CSharp.Syntax;
    using Microsoft.CodeAnalysis.Editing;
    using Microsoft.CodeAnalysis.Formatting;
    using Microsoft.CodeAnalysis.Simplification;

    [ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(AddToCompositeDisposableFix))]
    [Shared]
    internal class AddToCompositeDisposableFix : DocumentEditorCodeFixProvider
    {
        private static readonly TypeSyntax CompositeDisposableType = SyntaxFactory.QualifiedName(
                                                                                      SyntaxFactory.QualifiedName(
                                                                                          SyntaxFactory.QualifiedName(
                                                                                              SyntaxFactory.IdentifierName("System"),
                                                                                              SyntaxFactory.IdentifierName("Reactive")),
                                                                                          SyntaxFactory.IdentifierName("Disposables")),
                                                                                      SyntaxFactory.IdentifierName("CompositeDisposable"))
                                                                                  .WithAdditionalAnnotations(Simplifier.Annotation);

        public override ImmutableArray<string> FixableDiagnosticIds { get; } = ImmutableArray.Create(
            Descriptors.IDISP004DoNotIgnoreCreated.Id);

        protected override DocumentEditorFixAllProvider FixAllProvider() => null;

        protected override async Task RegisterCodeFixesAsync(DocumentEditorCodeFixContext context)
        {
            var syntaxRoot = await context.Document.GetSyntaxRootAsync(context.CancellationToken)
                                                   .ConfigureAwait(false);
            var semanticModel = await context.Document.GetSemanticModelAsync(context.CancellationToken)
                                                      .ConfigureAwait(false);
            if (semanticModel.Compilation.ReferencedAssemblyNames.Any(x => x.Name.Contains("System.Reactive")))
            {
                foreach (var diagnostic in context.Diagnostics)
                {
                    if (syntaxRoot.TryFindNodeOrAncestor(diagnostic, out ExpressionStatementSyntax statement))
                    {
                        if (TryGetField(statement, semanticModel, context.CancellationToken, out var field))
                        {
                            context.RegisterCodeFix(
                                $"Add to {field.Identifier.ValueText}.",
                                (editor, _) => AddToExisting(editor),
                                (string)null,
                                diagnostic);

                            void AddToExisting(DocumentEditor editor)
                            {
                                if (TryGetPreviousStatement(out var previous) &&
                                    TryGetCreateCompositeDisposable(out var compositeDisposableCreation))
                                {
                                    editor.RemoveNode(statement);
                                    editor.AddItemToCollectionInitializer(
                                        compositeDisposableCreation,
                                        statement.Expression,
                                        statement.GetTrailingTrivia());
                                }
                                else
                                {
                                    var code = editor.SemanticModel.UnderscoreFields()
                                        ? $"{field.Identifier.ValueText}.Add({statement.Expression})"
                                        : $"this.{field.Identifier.ValueText}.Add({statement.Expression})";

                                    _ = editor.ReplaceNode(
                                        statement.Expression,
                                        x => SyntaxFactory.ParseExpression(code)
                                                          .WithTriviaFrom(x));
                                }

                                bool TryGetPreviousStatement(out StatementSyntax result)
                                {
                                    result = null;
                                    if (statement.Parent is BlockSyntax block)
                                    {
                                        return block.Statements.TryElementAt(block.Statements.IndexOf(statement) - 1, out result);
                                    }

                                    return false;
                                }

                                bool TryGetCreateCompositeDisposable(out ObjectCreationExpressionSyntax result)
                                {
                                    if (previous is ExpressionStatementSyntax { Expression: AssignmentExpressionSyntax { Left: { } left, Right: ObjectCreationExpressionSyntax objectCreation } })
                                    {
                                        switch (left)
                                        {
                                            case IdentifierNameSyntax identifierName
                                                when identifierName.Identifier.ValueText == field.Identifier.ValueText:
                                            case MemberAccessExpressionSyntax { Expression: ThisExpressionSyntax _, Name: { } name }
                                                    when name.Identifier.ValueText == field.Identifier.ValueText:
                                                result = objectCreation;
                                                return true;
                                        }
                                    }

                                    result = null;
                                    return false;
                                }
                            }

                        }
                        else
                        {
                            context.RegisterCodeFix(
                                "Add to new CompositeDisposable.",
                                (editor, cancellationToken) => CreateAndInitialize(editor, cancellationToken),
                                (string)null,
                                diagnostic);

                            void CreateAndInitialize(DocumentEditor editor, CancellationToken cancellationToken)
                            {
                                var disposable = editor.AddField(
                                    statement.FirstAncestor<TypeDeclarationSyntax>(),
                                    "disposable",
                                    Accessibility.Private,
                                    DeclarationModifiers.ReadOnly,
                                    CompositeDisposableType,
                                    cancellationToken);

                                _ = editor.ReplaceNode(
                                    statement,
                                    x => x.WithExpression(
                                        SyntaxFactory.AssignmentExpression(
                                            SyntaxKind.SimpleAssignmentExpression,
                                            disposable,
                                            SyntaxFactory.ObjectCreationExpression(
                                                CompositeDisposableType,
                                                null,
                                                SyntaxFactory.InitializerExpression(
                                                    SyntaxKind.CollectionInitializerExpression,
                                                    SyntaxFactory.SeparatedList(
                                                        new[] { x.Expression.WithAdditionalAnnotations(Formatter.Annotation) },
                                                        new[] { SyntaxFactory.Token(default, SyntaxKind.CommaToken, x.GetTrailingTrivia()) })))))
                                          .WithoutTrailingTrivia()
                                          .WithAdditionalAnnotations(Formatter.Annotation));
                            }
                        }
                    }
                }
            }
        }

        private static bool TryGetField(SyntaxNode node, SemanticModel semanticModel, CancellationToken cancellationToken, out VariableDeclaratorSyntax field)
        {
            if (node.TryFirstAncestor(out TypeDeclarationSyntax containingType))
            {
                foreach (var member in containingType.Members)
                {
                    if (member is FieldDeclarationSyntax { Declaration: { Type: { } type, Variables: { Count: 1 } variables } } &&
                        semanticModel.TryGetType(type, cancellationToken, out var typeSymbol) &&
                        typeSymbol == KnownSymbol.CompositeDisposable)
                    {
                        field = variables[0];
                        return true;
                    }
                }
            }

            field = null;
            return false;
        }
    }
}
