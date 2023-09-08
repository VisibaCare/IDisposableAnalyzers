﻿namespace IDisposableAnalyzers;

using System.Threading;

using Gu.Roslyn.AnalyzerExtensions;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

internal static partial class Disposable
{
    internal static bool IsDisposedBefore(ISymbol symbol, ExpressionSyntax expression, SemanticModel semanticModel, IgnoredSymbols ignoredSymbols, CancellationToken cancellationToken)
    {
        if (Scope(expression) is { } block)
        {
            using var walker = InvocationWalker.Borrow(block);
            foreach (var invocation in walker.Invocations)
            {
                if (invocation.IsExecutedBefore(expression) == ExecutedBefore.No)
                {
                    continue;
                }

                if (DisposeCall.MatchAny(invocation, semanticModel, cancellationToken) is { } dispose &&
                    dispose.IsDisposing(symbol, semanticModel, cancellationToken) &&
                    !IsReassignedAfter(block, dispose))
                {
                    return true;
                }
            }
        }

        if (expression is AssignmentExpressionSyntax { Left: { } left } &&
            semanticModel.GetSymbolSafe(left, cancellationToken) is IPropertySymbol property &&
            property.TryGetSetter(cancellationToken, out var setter))
        {
            using var pooled = InvocationWalker.Borrow(setter);
            foreach (var invocation in pooled.Invocations)
            {
                if (DisposeCall.MatchAny(invocation, semanticModel, cancellationToken) is { } dispose &&
                    (dispose.IsDisposing(symbol, semanticModel, cancellationToken) ||
                     dispose.IsDisposing(property, semanticModel, cancellationToken)) &&
                    !IsReassignedAfter(setter, dispose))
                {
                    return true;
                }
            }
        }

        return false;

        static BlockSyntax? Scope(SyntaxNode node)
        {
            if (node.FirstAncestor<AnonymousFunctionExpressionSyntax>() is { Body: BlockSyntax lambdaBody })
            {
                return lambdaBody;
            }

            if (node.FirstAncestor<AccessorDeclarationSyntax>() is { Body: { } accessorBody })
            {
                return accessorBody;
            }

            if (node.FirstAncestor<BaseMethodDeclarationSyntax>() is { Body: { } methodBody })
            {
                return methodBody;
            }

            return null;
        }

        bool IsReassignedAfter(SyntaxNode scope, DisposeCall disposeCall)
        {
            using var walker = AssignmentWalker.Borrow(scope);
            foreach (var assignment in walker.Assignments)
            {
                if (assignment.TryFirstAncestor(out StatementSyntax? statement) &&
                    disposeCall.Invocation.IsExecutedBefore(statement) == ExecutedBefore.Yes &&
                    IsExecutedBefore(statement) &&
                    semanticModel.TryGetSymbol(assignment.Left, cancellationToken, out var assigned) &&
                    SymbolComparer.Equal(assigned, symbol) &&
                    IsCreation(assignment.Right, semanticModel, ignoredSymbols, cancellationToken))
                {
                    return true;
                }
            }

            return false;

            bool IsExecutedBefore(StatementSyntax assignStatement)
            {
                return assignStatement.IsExecutedBefore(expression) switch
                {
                    ExecutedBefore.Yes => true,
                    ExecutedBefore.No => false,
                    _ => WhenMaybe(),
                };

                bool WhenMaybe()
                {
                    if (assignStatement.Contains(expression))
                    {
                        return false;
                    }

                    if (disposeCall.Invocation.FirstAncestor<ExpressionStatementSyntax>() is { } disposeStatement &&
                        assignStatement.Parent == disposeStatement.Parent)
                    {
                        return true;
                    }

                    return false;
                }
            }
        }
    }
}
