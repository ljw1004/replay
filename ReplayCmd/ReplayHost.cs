using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

class ReplayHost
{
    public static async Task<Project> InstrumentAsync(Project originalProject, CancellationToken cancel = default(CancellationToken))
    {
        var originalComp = await originalProject.GetCompilationAsync(cancel);
        var replay = originalComp.GetTypeByMetadataName("System.Runtime.CompilerServices.Replay");

        try
        {
            var project = originalProject;
            foreach (var documentId in originalProject.DocumentIds)
            {
                var oldDocument = project.GetDocument(documentId);
                var oldTree = await oldDocument.GetSyntaxTreeAsync(cancel);
                var oldRoot = await oldTree.GetRootAsync(cancel);
                var rewriter = new TreeRewriter(originalComp, oldTree);
                var newRoot = rewriter.Visit(oldRoot);
                var newDocument = oldDocument.WithSyntaxRoot(newRoot);
                project = newDocument.Project;
            }
            return project;
        }
        catch (Exception ex)
        {
            throw;
        }

    }

    public static async Task<ImmutableDictionary<int,string>> RunAsync(Project project, Compilation comp, CancellationToken cancel = default(CancellationToken))
    {
        try
        {
            var fn = "";
            for (int i = 0; ; i++)
            {
                fn = Path.ChangeExtension(project.OutputFilePath, $".replay{(i == 0 ? "" : i.ToString())}.exe");
                if (File.Exists(fn)) try { File.Delete(fn); } catch (Exception) { }
                if (!File.Exists(fn)) break;
            }
            var compResult = await Task.Run(() => comp.Emit(fn, cancellationToken: cancel));
            if (!compResult.Success) return null;
            using (var p = new Process())
            using (var register = cancel.Register(() => p.Kill()))
            {
                p.StartInfo.FileName = fn;
                p.StartInfo.UseShellExecute = false;
                p.StartInfo.RedirectStandardOutput = true;
                p.StartInfo.RedirectStandardInput = true;
                p.StartInfo.CreateNoWindow = true;
                p.Start();
                // TODO: ReadToEndAsync should accept cancellation token
                var lines = (await p.StandardOutput.ReadToEndAsync()).Split(new[] { Environment.NewLine }, StringSplitOptions.RemoveEmptyEntries);
                p.WaitForExit();
                cancel.ThrowIfCancellationRequested();
                var builder = ImmutableDictionary.CreateBuilder<int, string>();
                foreach (var line in lines)
                {
                    cancel.ThrowIfCancellationRequested();
                    var separator = line.IndexOf(':');
                    int i;
                    if (separator != -1 && int.TryParse(line.Substring(0, separator), out i))
                    {
                        var data = line.Substring(separator + 1).Replace("\\r", "\r").Replace("\\n", "\n").Replace("\\\\", "\\");
                        if (data == "\0") data = null;
                        builder[i] = data;
                    }
                    else
                    {
                        builder[0] = $"error: {line}";
                    }
                }
                return builder.ToImmutable();
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            var builder = ImmutableDictionary.CreateBuilder<int, string>();
            builder.Add(0, $"error: {ex.Message}");
            return builder.ToImmutable();
        }

    }
}

class TreeRewriter : CSharpSyntaxRewriter
{
    Compilation Compilation;
    SyntaxTree OriginalTree;
    SemanticModel SemanticModel;
    INamedTypeSymbol ConsoleType;

    public TreeRewriter(Compilation compilation, SyntaxTree tree)
    {
        Compilation = compilation;
        OriginalTree = tree;
        SemanticModel = compilation.GetSemanticModel(tree);
        ConsoleType = Compilation.GetTypeByMetadataName("System.Console");
    }

    public override SyntaxNode VisitBlock(BlockSyntax node)
        => node.WithStatements(SyntaxFactory.List(ReplaceStatements(node.Statements)));

    public override SyntaxNode VisitSwitchSection(SwitchSectionSyntax node)
        => node.WithStatements(VisitList(SyntaxFactory.List(ReplaceStatements(node.Statements))));

    IEnumerable<StatementSyntax> ReplaceStatements(IEnumerable<StatementSyntax> statements)
    {
        foreach (var statement0 in statements)
        {
            var statement = Visit(statement0) as StatementSyntax;

            if (statement.IsKind(SyntaxKind.LocalDeclarationStatement))
            {
                var declaration = statement as LocalDeclarationStatementSyntax;
                if (declaration.Declaration.Variables.Count == 1)
                {
                    yield return statement;
                    var variable = declaration.Declaration.Variables[0];
                    if (variable.Initializer == null) continue;
                    var id = variable.Identifier.ValueText;
                    var loc = statement.GetLocation();
                    var mappedloc = loc.GetMappedLineSpan();
                    var s = $"class C {{ void f() {{ object {id}; System.Runtime.CompilerServices.Replay.Log({id}, \"{id}\", @\"{statement.GetLocation().SourceTree.FilePath}\", {mappedloc.StartLinePosition.Line}, 1); }} }}";
                    var call = CSharpSyntaxTree.ParseText(s).GetRoot().DescendantNodes().OfType<ExpressionStatementSyntax>().Single();
                    yield return call;
                    continue;
                }
            }
            else if (statement.IsKind(SyntaxKind.ExpressionStatement))
            {
                var expression = (statement as ExpressionStatementSyntax).Expression;
                if (expression.IsKind(SyntaxKind.InvocationExpression))
                {
                    var invocation = expression as InvocationExpressionSyntax;
                    var target = invocation.Expression;
                    var symbol = SemanticModel.GetSymbolInfo(target).Symbol as IMethodSymbol;
                    if (symbol != null && symbol.ContainingType == ConsoleType && symbol.Name == "WriteLine")
                    {
                        var loc = statement.GetLocation().GetMappedLineSpan();
                        var s = $"class C {{ void f() {{ System.Runtime.CompilerServices.Replay.WriteLine({loc.StartLinePosition.Line}, {string.Join(",", invocation.ArgumentList.Arguments)}); }} }}";
                        var call = CSharpSyntaxTree.ParseText(s).GetRoot().DescendantNodes().OfType<ExpressionStatementSyntax>().Single();
                        yield return statement;
                        yield return call;
                        continue;
                    }
                }
            }

            yield return statement;
        }
    }
}
