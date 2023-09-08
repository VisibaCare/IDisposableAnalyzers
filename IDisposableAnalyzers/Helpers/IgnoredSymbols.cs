// Adapted from https://github.com/dotnet/roslyn-analyzers/tree/main/src/Microsoft.CodeAnalysis.BannedApiAnalyzers
// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the MIT license.

#pragma warning disable

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;

namespace IDisposableAnalyzers
{
    internal class IgnoredSymbols
    {
        public static IgnoredSymbols Empty { get; } = new IgnoredSymbols(new(0));

        private readonly Dictionary<(string ContainerName, string SymbolName), List<IgnoreEntry>> _entries;

        private IgnoredSymbols(Dictionary<(string ContainerName, string SymbolName), List<IgnoreEntry>> entries)
        {
            _entries = entries;
        }

        public static IgnoredSymbols Read(CompilationStartAnalysisContext context)
        {
            var query =
                from additionalFile in context.Options.AdditionalFiles
                let fileName = Path.GetFileName(additionalFile.Path)
                where fileName != null && fileName.StartsWith("IgnoredIDisposableSymbols.") && fileName.EndsWith(".txt")
                orderby additionalFile.Path // Additional files are sorted by DocumentId (which is a GUID), make the file order deterministic
                let sourceText = additionalFile.GetText(context.CancellationToken)
                where sourceText != null
                from line in sourceText.Lines
                let text = line.ToString()
                let commentIndex = text.IndexOf("//", StringComparison.Ordinal)
                let textWithoutComment = commentIndex == -1 ? text : text[..commentIndex]
                where !string.IsNullOrWhiteSpace(textWithoutComment)
                let trimmedTextWithoutComment = textWithoutComment.TrimEnd()
                let span = commentIndex == -1 ? line.Span : new TextSpan(line.Span.Start, trimmedTextWithoutComment.Length)
                let entry = new IgnoreEntry(context.Compilation, trimmedTextWithoutComment, span, sourceText, additionalFile.Path)
                where !string.IsNullOrWhiteSpace(entry.DeclarationId)
                select entry;

            var entries = query.ToList();

            var fixedEntries = new Dictionary<(string ContainerName, string SymbolName), List<IgnoreEntry>>();
            foreach (var entry in entries)
            {
                var parsed = DocumentationCommentIdParser.ParseDeclaredSymbolId(entry.DeclarationId);
                if (parsed is null)
                {
                    continue;
                }

                if (!fixedEntries.TryGetValue(parsed.Value, out var existing))
                {
                    existing = new();
                    fixedEntries.Add(parsed.Value, existing);
                }

                existing.Add(entry);
            }

            return new IgnoredSymbols(fixedEntries);
        }

        public bool Contains(ISymbol? symbol)
        {
            if (symbol is { ContainingSymbol.Name: string parentName } &&
                _entries.TryGetValue((parentName, symbol.Name), out var entries))
            {
                foreach (var entry in entries)
                {
                    foreach (var ignoredSymbol in entry.Symbols)
                    {
                        if (SymbolEqualityComparer.Default.Equals(symbol, ignoredSymbol))
                        {
                            return true;
                        }
                    }
                }
            }

            return false;
        }

        private sealed class IgnoreEntry
        {
            public TextSpan Span { get; }
            public SourceText SourceText { get; }
            public string Path { get; }
            public string DeclarationId { get; }
            public string Message { get; }

            private readonly Lazy<ImmutableArray<ISymbol>> _lazySymbols;
            public ImmutableArray<ISymbol> Symbols => _lazySymbols.Value;

            public IgnoreEntry(Compilation compilation, string text, TextSpan span, SourceText sourceText, string path)
            {
                // Split the text on semicolon into declaration ID and message
                var index = text.IndexOf(';');

                if (index == -1)
                {
                    DeclarationId = text.Trim();
                    Message = "";
                }
                else if (index == text.Length - 1)
                {
                    DeclarationId = text[0..^1].Trim();
                    Message = "";
                }
                else
                {
                    DeclarationId = text[..index].Trim();
                    Message = text[(index + 1)..].Trim();
                }

                Span = span;
                SourceText = sourceText;
                Path = path;

                _lazySymbols = new Lazy<ImmutableArray<ISymbol>>(
                    () => DocumentationCommentId.GetSymbolsForDeclarationId(DeclarationId, compilation)
                        .SelectMany(ExpandConstituentNamespaces).ToImmutableArray());

                static IEnumerable<ISymbol> ExpandConstituentNamespaces(ISymbol symbol)
                {
                    if (symbol is not INamespaceSymbol namespaceSymbol)
                    {
                        yield return symbol;
                        yield break;
                    }

                    foreach (var constituent in namespaceSymbol.ConstituentNamespaces)
                        yield return constituent;
                }
            }

            public Location Location => Location.Create(Path, Span, SourceText.Lines.GetLinePositionSpan(Span));
        }
    }
}

#pragma warning enable
