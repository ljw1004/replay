using Microsoft.CodeAnalysis;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace MefRegistration
{
    using System.ComponentModel.Composition;
    using Microsoft.VisualStudio.Utilities;
    using Microsoft.VisualStudio.Text.Editor;

    [Export(typeof(IWpfTextViewCreationListener)), ContentType("text"), TextViewRole(PredefinedTextViewRoles.Document)]
    public sealed class ReplayAdornmentTextViewCreationListener : IWpfTextViewCreationListener
    {
        // This class will be instantiated the first time a text document is opened. (That's because our VSIX manifest
        // lists this project, i.e. this DLL, and VS scans all such listed DLLs to find all types with the right attributes).
        // The TextViewCreated event will be raised each time a text document tab is created. It won't be
        // raised for subsequent re-activation of an existing document tab.
        public void TextViewCreated(IWpfTextView textView) => new ReplayAdornment(textView);

#pragma warning disable CS0169 // C# warning "the field editorAdornmentLayer is never used" -- but it is used, by MEF!
        [Export(typeof(AdornmentLayerDefinition)), Name(nameof(ReplayAdornment)), Order(After = PredefinedAdornmentLayers.Selection, Before = Microsoft.VisualStudio.Text.Editor.PredefinedAdornmentLayers.Text)]
        private AdornmentLayerDefinition editorAdornmentLayer;
#pragma warning restore CS0169
    }
}


public sealed class ReplayAdornment
{
    // VS integration
    readonly Microsoft.VisualStudio.Text.Editor.IWpfTextView View;
    Workspace Workspace;
    DocumentId DocumentId;

    // Replay
    ReplayManager ReplayManager;
    VersionStamp ReplayDocumentVersionStamp;



    public ReplayAdornment(Microsoft.VisualStudio.Text.Editor.IWpfTextView view)
    {
        var workspace = Microsoft.CodeAnalysis.MSBuild.MSBuildWorkspace.Create();

        var componentModel = (Microsoft.VisualStudio.ComponentModelHost.IComponentModel)Microsoft.VisualStudio.Shell.Package.GetGlobalService(typeof(Microsoft.VisualStudio.ComponentModelHost.SComponentModel));
        Workspace = componentModel.GetService<Microsoft.VisualStudio.LanguageServices.VisualStudioWorkspace>();

        View = view;
        View.LayoutChanged += OnLayoutChanged;
        View.ViewportWidthChanged += OnViewportWidthChanged;
        View.Closed += OnClosed;
    }

    private void OnClosed(object sender, EventArgs e)
    {
        ReplayManager.ResultsChanged -= OnReplayResultsChanged;
        if (ReplayManager != null) ReplayManager.Dispose(); ReplayManager = null;
    }

    internal void OnLayoutChanged(object sender, Microsoft.VisualStudio.Text.Editor.TextViewLayoutChangedEventArgs e)
    {
        // Raised whenever the rendered text displayed in the ITextView changes - whenever the view does a layout
        // (which happens when DisplayTextLineContainingBufferPosition is called or in response to text or classification
        // changes), and also when the view scrolls or when its size changes.
        // Responsible for adding the adornment to any reformatted lines.

        if (DocumentId == null)
        {
            var dte = Microsoft.VisualStudio.Shell.Package.GetGlobalService(typeof(EnvDTE.DTE)) as EnvDTE.DTE;
            var activeDocument = dte?.ActiveDocument; // sometimes we're constructed/invoked before ActiveDocument has been set
            if (activeDocument != null) DocumentId = Workspace.CurrentSolution.GetDocumentIdsWithFilePath(activeDocument.FullName).FirstOrDefault();
            if (DocumentId == null) return;
        }

        var document = Workspace.CurrentSolution.GetDocument(DocumentId);
        if (document == null) return;
        if (ReplayManager == null)
        {
            ReplayManager = ReplayManager.ForProject(document.Project.FilePath);
            ReplayManager.ResultsChanged += OnReplayResultsChanged;
        }
        VersionStamp docVersion; if (!document.TryGetTextVersion(out docVersion)) return;
        if (docVersion != ReplayDocumentVersionStamp)
        {
            ReplayDocumentVersionStamp = docVersion;
            ReplayManager.TriggerReplayAsync(document.Project);
        }

        CreateAdornments(e.NewOrReformattedLines);
    }

    void CreateAdornments(IEnumerable<Microsoft.VisualStudio.Text.Formatting.ITextViewLine> lines)
    {
        foreach (var eline in lines)
        {
            int iline = eline.End.GetContainingLine().LineNumber;
            string msg; if (!ReplayManager.Results.TryGetValue(iline, out msg)) continue;
            var geometry = View.TextViewLines.GetMarkerGeometry(eline.Extent);
            if (geometry == null) continue;
            var adornment = new TextBlock { Width = 240, Height = geometry.Bounds.Height, Background = Brushes.Yellow, Opacity = 0.2, Text = $"// {msg}" };
            Canvas.SetLeft(adornment, View.ViewportWidth - adornment.Width);
            Canvas.SetTop(adornment, geometry.Bounds.Top);
            View.GetAdornmentLayer(nameof(ReplayAdornment)).AddAdornment(Microsoft.VisualStudio.Text.Editor.AdornmentPositioningBehavior.TextRelative, eline.Extent, null, adornment, null);
        }
    }


    private void OnReplayResultsChanged()
    {
        View.VisualElement.Dispatcher.BeginInvoke((Action)delegate
        {
            View.GetAdornmentLayer(nameof(ReplayAdornment)).RemoveAllAdornments();
            CreateAdornments(View.TextViewLines);
        });
    }

    private void OnViewportWidthChanged(object sender, EventArgs e)
    {
        var elements = View?.GetAdornmentLayer(nameof(ReplayAdornment))?.Elements;
        if (elements == null) return;
        foreach (var element in elements)
        {
            var adornment = element.Adornment as FrameworkElement;
            if (adornment != null) Canvas.SetLeft(adornment, View.ViewportWidth - adornment.Width);
        }
    }

}


class ReplayManager : IDisposable
{
    static Dictionary<string, ReplayManager> projects = new Dictionary<string, ReplayManager>();

    int RefCount;
    string FilePath;
    CancellationTokenSource Cancel;
    Task Task;
    public ImmutableDictionary<int, string> Results = ImmutableDictionary<int, string>.Empty;
    public event Action ResultsChanged;

    public void TriggerReplayAsync(Project project)
    {
        try { Cancel?.Cancel(); } catch (Exception) { Debug.WriteLine("oops"); }
        Cancel = new CancellationTokenSource();
        Task = ReplayInnerAsync(project, Task, Cancel.Token);
    }

    async Task ReplayInnerAsync(Project project, Task prevTask, CancellationToken cancel)
    {
        if (prevTask != null) try { await prevTask; } catch (Exception) { }
        if (project == null) return;
        if (!File.Exists(project.OutputFilePath)) return; // if the user has done at least one build, then all needed DLLs will likely be in place

        var project2 = await ReplayHost.InstrumentAsync(project, cancel);
        var comp2 = await project2.GetCompilationAsync(cancel);
        var results = await ReplayHost.RunAsync(project2, comp2, cancel);
        if (results == null) return; // don't override in case of error
        Results = results;
        ResultsChanged?.Invoke();
    }

    public static ReplayManager ForProject(string filePath)
    {
        if (filePath == null) return null;
        lock (projects)
        {
            ReplayManager r;
            if (projects.TryGetValue(filePath, out r)) { r.RefCount++; return r; }
            r = new ReplayManager { RefCount = 1, FilePath = filePath };
            projects[filePath] = r;
            return r;
        }
    }

    public void Dispose()
    {
        // TODO: this is weird that I'm using the same instance of ReplayManager. Imagine if someone
        // calls ForProject() and then disposes of it twice. The proper result is that they should only
        // decrement the refcount once (since Dispose is meant to be idempotent). But what will happen
        // here is it'll be decremented twice.
        if (FilePath == null) return;
        lock (this)
        {
            RefCount--;
            if (RefCount == 0) { lock (projects) projects.Remove(FilePath); FilePath = null; Cancel?.Cancel(); }
        }
    }

}

