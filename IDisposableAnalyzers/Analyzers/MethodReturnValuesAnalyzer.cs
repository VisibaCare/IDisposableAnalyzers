﻿namespace IDisposableAnalyzers;

using System.Collections.Immutable;
using Gu.Roslyn.AnalyzerExtensions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
internal class MethodReturnValuesAnalyzer : DiagnosticAnalyzer
{
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = ImmutableArray.Create(
        Descriptors.IDISP015DoNotReturnCachedAndCreated);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(compilationContext =>
        {
            var ignoredSymbols = IgnoredSymbols.Read(compilationContext);
            context.RegisterSyntaxNodeAction(c => Handle(c, ignoredSymbols), SyntaxKind.MethodDeclaration, SyntaxKind.GetAccessorDeclaration);
        });
    }

    private static void Handle(SyntaxNodeAnalysisContext context, IgnoredSymbols ignoredSymbols)
    {
        if (context.ContainingSymbol is IMethodSymbol method &&
            context.Node is MethodDeclarationSyntax methodDeclaration &&
            Disposable.IsAssignableFrom(method.ReturnType, context.Compilation))
        {
            using var walker = ReturnValueWalker.Borrow(methodDeclaration, ReturnValueSearch.RecursiveInside, context.SemanticModel, context.CancellationToken);
            if (walker.Values.TryFirst(x => IsCreated(x), out _) &&
                walker.Values.TryFirst(x => IsCachedOrInjected(x) && !IsNop(x), out _))
            {
                context.ReportDiagnostic(Diagnostic.Create(Descriptors.IDISP015DoNotReturnCachedAndCreated, methodDeclaration.Identifier.GetLocation()));
            }
        }

        bool IsCreated(ExpressionSyntax expression)
        {
            return Disposable.IsCreation(expression, context.SemanticModel, ignoredSymbols, context.CancellationToken);
        }

        bool IsCachedOrInjected(ExpressionSyntax expression)
        {
            return Disposable.IsCachedOrInjected(expression, expression, context.SemanticModel, context.CancellationToken);
        }

        bool IsNop(ExpressionSyntax expression)
        {
            return Disposable.IsNop(expression, context.SemanticModel, context.CancellationToken);
        }
    }
}
