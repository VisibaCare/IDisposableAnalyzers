﻿namespace IDisposableAnalyzers
{
    using System;
    using System.Diagnostics.CodeAnalysis;
    using System.Linq;
    using System.Threading;
    using Gu.Roslyn.AnalyzerExtensions;
    using Gu.Roslyn.CodeFixExtensions;
    using Microsoft.CodeAnalysis;
    using Microsoft.CodeAnalysis.CSharp;
    using Microsoft.CodeAnalysis.CSharp.Syntax;
    using Microsoft.CodeAnalysis.Formatting;
    using Microsoft.CodeAnalysis.Simplification;

    // ReSharper disable once InconsistentNaming
    internal static class IDisposableFactory
    {
        internal static readonly TypeSyntax SystemIDisposable =
            SyntaxFactory.QualifiedName(SyntaxFactory.IdentifierName("System"), SyntaxFactory.IdentifierName("IDisposable"))
                         .WithAdditionalAnnotations(Simplifier.Annotation);

        internal static readonly TypeSyntax SystemIAsyncDisposable =
            SyntaxFactory.QualifiedName(SyntaxFactory.IdentifierName("System"), SyntaxFactory.IdentifierName("IAsyncDisposable"))
                         .WithAdditionalAnnotations(Simplifier.Annotation);

        internal static readonly StatementSyntax GcSuppressFinalizeThis =
            SyntaxFactory.ExpressionStatement(
                             SyntaxFactory.InvocationExpression(
                                 SyntaxFactory.MemberAccessExpression(
                                     SyntaxKind.SimpleMemberAccessExpression,
                                     SyntaxFactory.QualifiedName(SyntaxFactory.IdentifierName("System"), SyntaxFactory.IdentifierName("GC")),
                                     SyntaxFactory.IdentifierName("SuppressFinalize")),
                                 Arguments(SyntaxFactory.ThisExpression())))
                         .WithAdditionalAnnotations(Simplifier.Annotation);

        private static readonly IdentifierNameSyntax Dispose = SyntaxFactory.IdentifierName("Dispose");

        internal static ExpressionStatementSyntax DisposeStatement(ExpressionSyntax disposable)
        {
            return SyntaxFactory.ExpressionStatement(
                SyntaxFactory.InvocationExpression(
                    SyntaxFactory.MemberAccessExpression(
                        SyntaxKind.SimpleMemberAccessExpression,
                        disposable,
                        Dispose)));
        }

        internal static ExpressionStatementSyntax ConditionalDisposeStatement(ExpressionSyntax disposable)
        {
            return SyntaxFactory.ExpressionStatement(
                SyntaxFactory.ConditionalAccessExpression(
                    disposable,
                    SyntaxFactory.InvocationExpression(
                        SyntaxFactory.MemberBindingExpression(SyntaxFactory.Token(SyntaxKind.DotToken), Dispose))));
        }

        internal static ExpressionStatementSyntax DisposeStatement(ExpressionSyntax disposable, SemanticModel semanticModel, CancellationToken cancellationToken)
        {
            return SyntaxFactory.ExpressionStatement(
                SyntaxFactory.ConditionalAccessExpression(
                    Normalize(MemberAccess(disposable)),
                    SyntaxFactory.InvocationExpression(
                        SyntaxFactory.MemberBindingExpression(SyntaxFactory.Token(SyntaxKind.DotToken), Dispose))));

            ExpressionSyntax MemberAccess(ExpressionSyntax e)
            {
                switch (e)
                {
                    case { Parent: ArgumentSyntax { RefOrOutKeyword: { } refOrOut } }
                        when !refOrOut.IsKind(SyntaxKind.None):
                        return e;
                    case IdentifierNameSyntax _:
                    case MemberAccessExpressionSyntax { Expression: ThisExpressionSyntax _, Name: { } }:
                        if (semanticModel.GetSymbolInfo(e, cancellationToken).Symbol is IPropertySymbol { GetMethod: { } get } &&
                            get.TrySingleAccessorDeclaration(cancellationToken, out var getter))
                        {
                            switch (getter)
                            {
                                case { ExpressionBody: { Expression: { } expression } }:
                                    return expression;
                                case { Body: { Statements: { Count: 1 } statements } }
                                    when statements[0] is ReturnStatementSyntax { Expression: { } expression }:
                                    return expression;
                            }
                        }

                        return e;
                    default:
                        return e;
                }
            }

            ExpressionSyntax Normalize(ExpressionSyntax e)
            {
                if (semanticModel.ClassifyConversion(e, KnownSymbol.IDisposable.GetTypeSymbol(semanticModel.Compilation)).IsImplicit)
                {
                    if (semanticModel.TryGetType(e, cancellationToken, out var type) &&
                        DisposeMethod.Find(type, semanticModel.Compilation, Search.Recursive) is { ExplicitInterfaceImplementations: { IsEmpty: true } })
                    {
                        return e.WithoutTrivia()
                                .WithLeadingElasticLineFeed();
                    }

                    return SyntaxFactory.ParenthesizedExpression(SyntaxFactory.CastExpression(SystemIDisposable, e));
                }

                return AsIDisposable(e.WithoutTrivia())
                                      .WithLeadingElasticLineFeed();
            }
        }

        internal static ExpressionStatementSyntax DisposeAsyncStatement(FieldOrProperty disposable, SemanticModel semanticModel, CancellationToken cancellationToken)
        {
            switch (MemberAccessContext.Create(disposable, semanticModel, cancellationToken))
            {
                case { NeverNull: { } neverNull }:
                    if (disposable.Type.IsAssignableTo(KnownSymbol.IAsyncDisposable, semanticModel.Compilation) &&
                        DisposeMethod.FindDisposeAsync(disposable.Type, semanticModel.Compilation, Search.Recursive) is { ExplicitInterfaceImplementations: { IsEmpty: true } })
                    {
                        return AsyncDisposeStatement(neverNull.WithoutTrivia()).WithLeadingElasticLineFeed();
                    }

                    return AsyncDisposeStatement(
                            SyntaxFactory.CastExpression(
                                SystemIAsyncDisposable,
                                neverNull.WithoutTrivia()))
                        .WithLeadingElasticLineFeed();

                    static ExpressionStatementSyntax AsyncDisposeStatement(ExpressionSyntax expression)
                    {
                        return SyntaxFactory.ExpressionStatement(
                            SyntaxFactory.AwaitExpression(
                                expression: SyntaxFactory.InvocationExpression(
                                    expression: SyntaxFactory.MemberAccessExpression(
                                        kind: SyntaxKind.SimpleMemberAccessExpression,
                                        expression: expression,
                                        name: SyntaxFactory.IdentifierName("DisposeAsync")),
                                    argumentList: SyntaxFactory.ArgumentList())));
                    }

                default:
                    throw new InvalidOperationException("Error generating DisposeAsyncStatement.");
            }
        }

        internal static ExpressionStatementSyntax DisposeStatement(FieldOrProperty disposable, SemanticModel semanticModel, CancellationToken cancellationToken)
        {
            switch (MemberAccessContext.Create(disposable, semanticModel, cancellationToken))
            {
                case { NeverNull: { } neverNull }:
                    if (disposable.Type.IsAssignableTo(KnownSymbol.IDisposable, semanticModel.Compilation) &&
                        DisposeMethod.Find(disposable.Type, semanticModel.Compilation, Search.Recursive) is { ExplicitInterfaceImplementations: { IsEmpty: true } })
                    {
                        return DisposeStatement(neverNull.WithoutTrivia()).WithLeadingElasticLineFeed();
                    }

                    return DisposeStatement(
                            SyntaxFactory.CastExpression(
                                SystemIDisposable,
                                neverNull.WithoutTrivia()))
                        .WithLeadingElasticLineFeed();
                case { MaybeNull: { } maybeNull }:
                    if (DisposeMethod.IsAccessibleOn(disposable.Type, semanticModel.Compilation))
                    {
                        return ConditionalDisposeStatement(maybeNull).WithLeadingElasticLineFeed();
                    }

                    return ConditionalDisposeStatement(
                            SyntaxFactory.BinaryExpression(
                                SyntaxKind.AsExpression,
                                maybeNull,
                                SystemIDisposable))
                        .WithLeadingElasticLineFeed();
                default:
                    throw new InvalidOperationException("Error generating DisposeStatement.");
            }
        }

        internal static ExpressionSyntax MemberAccess(SyntaxToken memberIdentifier, SemanticModel semanticModel, CancellationToken cancellationToken)
        {
            if (semanticModel.SyntaxTree.TryGetRoot(out var root) &&
                semanticModel.GetSymbolSafe(memberIdentifier.Parent, cancellationToken) is { } member &&
                FieldOrProperty.TryCreate(member, out var fieldOrProperty) &&
                TryGetMemberAccessFromUsage(root, fieldOrProperty, semanticModel, cancellationToken, out var memberAccess))
            {
                return memberAccess;
            }

            return semanticModel.UnderscoreFields() == CodeStyleResult.Yes
                ? (ExpressionSyntax)SyntaxFactory.IdentifierName(memberIdentifier)
                : SyntaxFactory.MemberAccessExpression(
                    SyntaxKind.SimpleMemberAccessExpression,
                    SyntaxFactory.ThisExpression(),
                    SyntaxFactory.IdentifierName(memberIdentifier));
        }

        internal static ExpressionSyntax MemberAccess(FieldOrProperty member, SemanticModel semanticModel, CancellationToken cancellationToken)
        {
            if (semanticModel.SyntaxTree.TryGetRoot(out var root) &&
                TryGetMemberAccessFromUsage(root, member, semanticModel, cancellationToken, out var memberAccess))
            {
                return memberAccess;
            }

            return Create(
                SyntaxFacts.GetKeywordKind(member.Name) != SyntaxKind.None
                    ? SyntaxFactory.VerbatimIdentifier(default, $"@{member.Name}", member.Name, default)
                    : SyntaxFactory.Identifier(member.Name));

            ExpressionSyntax Create(SyntaxToken identifier)
            {
                return semanticModel.UnderscoreFields() == CodeStyleResult.Yes
                    ? (ExpressionSyntax)SyntaxFactory.IdentifierName(identifier)
                    : SyntaxFactory.MemberAccessExpression(
                        SyntaxKind.SimpleMemberAccessExpression,
                        SyntaxFactory.ThisExpression(),
                        SyntaxFactory.IdentifierName(identifier));
            }
        }

        internal static bool TryGetMemberAccessFromUsage(SyntaxNode containingNode, FieldOrProperty member, SemanticModel semanticModel, CancellationToken cancellationToken, [NotNullWhen(true)] out ExpressionSyntax? expression)
        {
            using (var identifierNameWalker = IdentifierNameWalker.Borrow(containingNode))
            {
                foreach (var name in identifierNameWalker.IdentifierNames)
                {
                    if (name.Identifier.ValueText == member.Name &&
                        semanticModel.TryGetSymbol(name, cancellationToken, out var symbol) &&
                        symbol.Equals(member.Symbol))
                    {
                        switch (name)
                        {
                            case { Parent: MemberAccessExpressionSyntax { Expression: ThisExpressionSyntax _ } memberAccess }:
                                expression = memberAccess;
                                return true;
                            case { Parent: ArgumentSyntax _ }:
                            case { Parent: ExpressionSyntax _ }:
                                expression = name;
                                return true;
                        }
                    }
                }
            }

            expression = null;
            return false;
        }

        internal static AnonymousFunctionExpressionSyntax PrependStatements(this AnonymousFunctionExpressionSyntax lambda, params StatementSyntax[] statements)
        {
            return lambda switch
            {
                { Body: ExpressionSyntax body } => lambda.ReplaceNode(
                                                             body,
                                                             SyntaxFactory.Block(statements.Append(SyntaxFactory.ExpressionStatement(body)))
                                                                          .WithLeadingLineFeed())
                                                         .WithAdditionalAnnotations(Formatter.Annotation),
                { Body: BlockSyntax block } => lambda.ReplaceNode(block, block.AddStatements(statements)),
                _ => throw new NotSupportedException(
                    $"No support for adding statements to lambda with the shape: {lambda?.ToString() ?? "null"}"),
            };
        }

        internal static MethodDeclarationSyntax AsBlockBody(this MethodDeclarationSyntax method, params StatementSyntax[] statements)
        {
            return method.WithExpressionBody(null)
                         .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.None))
                         .WithBody(SyntaxFactory.Block(statements));
        }

        internal static ParenthesizedExpressionSyntax AsIDisposable(ExpressionSyntax e)
        {
            return SyntaxFactory.ParenthesizedExpression(SyntaxFactory.BinaryExpression(SyntaxKind.AsExpression, e, SystemIDisposable));
        }

        internal static ArgumentListSyntax Arguments(ExpressionSyntax expression)
        {
            return SyntaxFactory.ArgumentList(SyntaxFactory.SingletonSeparatedList(SyntaxFactory.Argument(expression)));
        }

        private struct MemberAccessContext
        {
            internal readonly ExpressionSyntax? NeverNull;
            internal readonly ExpressionSyntax? MaybeNull;

            private MemberAccessContext(ExpressionSyntax? neverNull, ExpressionSyntax? maybeNull)
            {
                this.NeverNull = neverNull;
                this.MaybeNull = maybeNull;
            }

            internal static MemberAccessContext Create(FieldOrProperty disposable, SemanticModel semanticModel, CancellationToken cancellationToken)
            {
                using (var walker = MutationWalker.For(disposable, semanticModel, cancellationToken))
                {
                    if (walker.TrySingle(out var mutation) &&
                        mutation is AssignmentExpressionSyntax { Left: { } single, Right: ObjectCreationExpressionSyntax _, Parent: ExpressionStatementSyntax { Parent: BlockSyntax { Parent: ConstructorDeclarationSyntax _ } } } &&
                        disposable.Symbol.ContainingType.Constructors.Length == 1)
                    {
                        return new MemberAccessContext(single.WithoutTrivia(), null);
                    }

                    if (walker.IsEmpty &&
                        disposable.Initializer(cancellationToken) is { Value: ObjectCreationExpressionSyntax _ })
                    {
                        return new MemberAccessContext(
                            MemberAccess(disposable, semanticModel, cancellationToken),
                            null);
                    }

                    if (walker.Assignments.TryFirst(out var first))
                    {
                        return new MemberAccessContext(null, first.Left.WithoutTrivia());
                    }
                }

                return new MemberAccessContext(
                    null,
                    MemberAccess(disposable, semanticModel, cancellationToken));
            }
        }
    }
}
