﻿using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Emit;
using Typewriter.VisualStudio;

namespace Typewriter.TemplateEditor.Lexing.Roslyn
{
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Design", "CA1001:TypesThatOwnDisposableFieldsShouldBeDisposable")]
    public class ShadowClass
    {
        #region Constants

        private const string startTemplate = @"namespace __Typewriter
{
    using System;
    using System.Linq;
    using System.Collections.Generic;
    using Typewriter.CodeModel;
    using Attribute = Typewriter.CodeModel.Attribute;
    using Enum = Typewriter.CodeModel.Enum;
    using Type = Typewriter.CodeModel.Type;
    ";

        private const string classTemplate = @"
    public class __Code
    {
";

        private const string endClassTemplate = @"
    }";

        private const string endTemplate = @"
}
";
        #endregion

        private readonly ShadowWorkspace workspace;
        private readonly DocumentId documentId;
        private readonly List<Snippet> snippets = new List<Snippet>();
        
        private int offset;
        private bool classAdded;

        public ShadowClass()
        {
            workspace = new ShadowWorkspace();
            documentId = workspace.AddProjectWithDocument("ShadowClass.cs", "");
        }

        public void AddUsing(string code, int startIndex)
        {
            snippets.Add(Snippet.Create(SnippetType.Using, code, offset, startIndex, startIndex + code.Length));
            offset += code.Length;
        }

        public void AddBlock(string code, int startIndex)
        {
            if (classAdded == false)
            {
                snippets.Add(Snippet.Create(SnippetType.Class, classTemplate));
                offset += classTemplate.Length;
                classAdded = true;
            }

            snippets.Add(Snippet.Create(SnippetType.Code, code, offset, startIndex, startIndex + code.Length));
            offset += code.Length;
        }

        public void AddLambda(string code, string type, string name, int startIndex)
        {
            if (classAdded == false)
            {
                snippets.Add(Snippet.Create(SnippetType.Class, classTemplate));
                offset += classTemplate.Length;
                classAdded = true;
            }

            var method = $"bool __{startIndex} ({type} {name}) {{ return ";
            var index = code.IndexOf("=>", StringComparison.Ordinal) + 2;
            code = code.Remove(0, index);

            snippets.Add(Snippet.Create(SnippetType.Class, method));
            offset += method.Length;

            snippets.Add(Snippet.Create(SnippetType.Lambda, code, offset, startIndex, startIndex + code.Length, index));
            offset += code.Length;

            snippets.Add(Snippet.Create(SnippetType.Class, ";}"));
            offset += 2;
        }

        public void Clear()
        {
            snippets.Clear();
            snippets.Add(Snippet.Create(SnippetType.Class, startTemplate));
            offset = startTemplate.Length;
            classAdded = false;
        }

        public void Parse()
        {
            if (classAdded)
            {
                snippets.Add(Snippet.Create(SnippetType.Class, endClassTemplate));
            }

            snippets.Add(Snippet.Create(SnippetType.Class, endTemplate));

            var code = string.Join(string.Empty, snippets.Select(s => s.Code));
            workspace.UpdateText(documentId, code);
        }

        public IEnumerable<Token> GetTokens()
        {
            var tokens = snippets.Where(s => s.Type != SnippetType.Class).SelectMany(s =>
            {
                var classifiedSpans = workspace.GetClassifiedSpans(documentId, s.Offset, s.Length);

                return classifiedSpans.Select(span => new Token
                {
                    Classification = GetClassification(span.ClassificationType),
                    Start = s.FromShadowIndex(span.TextSpan.Start),
                    Length = span.TextSpan.Length
                });
            });

            return tokens.Where(t => t.Classification != null);
        }

        public IEnumerable<Token> GetErrorTokens()
        {
            var tokens = snippets.Where(s => s.Type != SnippetType.Class).SelectMany(s =>
            {
                var diagnostics = workspace.GetDiagnostics(documentId, s.Offset, s.Length);

                return diagnostics.Select(diagnostic => new Token
                {
                    QuickInfo = diagnostic.GetMessage(),
                    Start = s.FromShadowIndex(diagnostic.Location.SourceSpan.Start),
                    Length = diagnostic.Location.SourceSpan.Length
                });
            });

            return tokens;
        }

        public IEnumerable<TemporaryIdentifier> GetIdentifiers()
        {
            var methods = workspace.GetMethods(documentId);

            foreach (var method in methods)
            {
                var returnType = method.ReturnType.ToString();
                var identifier = method.Identifier.ToString();
                var parameter = method.ParameterList.Parameters.FirstOrDefault()?.Type.ToString();

                if (identifier.StartsWith("__") == false && parameter != null)
                {
                    var context = Contexts.Find(parameter);
                    if (context != null)
                    {
                        var isBoolean = returnType == "bool" || returnType == "Boolean";

                        var index = returnType.IndexOf("Collection", StringComparison.Ordinal);
                        var childContext = Contexts.Find(index > 0 ? returnType.Substring(0, index) : returnType)?.Name;
                        var isCollection = childContext != null && index > 0;

                        yield return new TemporaryIdentifier(context, new Identifier
                        {
                            Name = identifier,
                            QuickInfo = "(extension) " + (isCollection ? "collection" : returnType.ToLowerInvariant()) + " " + identifier,
                            Context = childContext,
                            HasContext = childContext != null,
                            IsBoolean = isBoolean,
                            IsCollection = isCollection,
                            RequireTemplate = isCollection,
                            IsCustom = true
                        });
                    }
                }
            }
        }

        public ISymbol GetSymbol(int position)
        {
            var snippet = snippets.FirstOrDefault(s => s.Contains(position));

            if (snippet == null) return null;

            return workspace.GetSymbol(documentId, snippet.ToShadowIndex(position));
        }

        public IEnumerable<ISymbol> GetRecommendedSymbols(int position)
        {
            var snippet = snippets.FirstOrDefault(s => s.Contains(position));

            if (snippet == null) return new ISymbol[0];

            return workspace.GetRecommendedSymbols(documentId, snippet.ToShadowIndex(position)).Where(s => s.Name.StartsWith("__") == false);
        }

        public EmitResult Compile(string outputPath)
        {
            workspace.ChangeAllMethodsToPublicStatic(documentId);
            return workspace.Compile(documentId, outputPath);
        }

        private static string GetClassification(string classificationType)
        {
            switch (classificationType)
            {
                case "keyword":
                    return Classifications.Keyword;

                case "class name":
                case "interface name":
                    return Classifications.Property;

                case "identifier":
                    return Classifications.Identifier;

                case "string":
                    return Classifications.String;

                case "comment":
                    return Classifications.Comment;
            }

            return null;
        }
    }
}