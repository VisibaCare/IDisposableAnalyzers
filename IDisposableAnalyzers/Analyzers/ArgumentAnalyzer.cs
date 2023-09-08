﻿namespace IDisposableAnalyzers;

using System.Collections.Immutable;
using System.Threading;

using Gu.Roslyn.AnalyzerExtensions;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
internal class ArgumentAnalyzer : DiagnosticAnalyzer
{
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = ImmutableArray.Create(
        Descriptors.IDISP001DisposeCreated,
        Descriptors.IDISP003DisposeBeforeReassigning);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(compilationContext =>
        {
            var ignoredSymbols = IgnoredSymbols.Read(compilationContext);
            context.RegisterSyntaxNodeAction(c => Handle(c, ignoredSymbols), SyntaxKind.Argument);
        });
    }

    private static void Handle(SyntaxNodeAnalysisContext context, IgnoredSymbols ignoredSymbols)
    {
        if (!context.IsExcludedFromAnalysis() &&
            context.Node is ArgumentSyntax { Parent: ArgumentListSyntax { Parent: InvocationExpressionSyntax invocation } } argument &&
            argument.RefOrOutKeyword.IsEither(SyntaxKind.RefKeyword, SyntaxKind.OutKeyword) &&
            IsCreation(argument, context.SemanticModel, ignoredSymbols, context.CancellationToken) &&
            context.SemanticModel.TryGetSymbol(argument.Expression, context.CancellationToken, out var symbol))
        {
            if (symbol.Kind == SymbolKind.Discard ||
                (LocalOrParameter.TryCreate(symbol, out var localOrParameter) &&
                 Disposable.ShouldDispose(localOrParameter, context.SemanticModel, ignoredSymbols, context.CancellationToken)))
            {
                context.ReportDiagnostic(Diagnostic.Create(Descriptors.IDISP001DisposeCreated, argument.GetLocation()));
            }

            if (Disposable.IsAssignedWithCreated(symbol, invocation, context.SemanticModel, ignoredSymbols, context.CancellationToken) &&
                !Disposable.IsDisposedBefore(symbol, invocation, context.SemanticModel, ignoredSymbols, context.CancellationToken))
            {
                context.ReportDiagnostic(Diagnostic.Create(Descriptors.IDISP003DisposeBeforeReassigning, argument.GetLocation()));
            }
        }
    }

    private static bool IsCreation(ArgumentSyntax candidate, SemanticModel semanticModel, IgnoredSymbols ignoredSymbols, CancellationToken cancellationToken)
    {
        if (candidate.Parent is ArgumentListSyntax { Parent: InvocationExpressionSyntax invocation } &&
            semanticModel.TryGetSymbol(invocation, cancellationToken, out var method) &&
            method.ContainingType != KnownSymbols.Interlocked &&
            method.TryFindParameter(candidate, out var parameter) &&
            Disposable.IsPotentiallyAssignableFrom(parameter.Type, semanticModel.Compilation))
        {
            using var walker = AssignedValueWalker.Borrow(candidate.Expression, semanticModel, cancellationToken);
            using var recursive = RecursiveValues.Borrow(walker.Values, semanticModel, cancellationToken);
            return Disposable.IsAnyCreation(recursive, semanticModel, ignoredSymbols, cancellationToken) &&
                  !Disposable.IsAnyCachedOrInjected(recursive, semanticModel, cancellationToken);
        }

        return false;
    }
}
