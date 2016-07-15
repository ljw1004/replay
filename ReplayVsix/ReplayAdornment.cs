using Microsoft.CodeAnalysis;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Formatting;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Dataflow;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace MefRegistration
{
    using System.ComponentModel.Composition;
    using Microsoft.VisualStudio.Utilities;
    using Microsoft.VisualStudio.Text.Editor;

    [Export(typeof(IWpfTextViewCreationListener)), ContentType("text"), TextViewRole(PredefinedTextViewRoles.Document)]
    internal sealed class ReplayAdornmentTextViewCreationListener : IWpfTextViewCreationListener
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


internal sealed class ReplayAdornment
{
    // VS integration
    readonly Microsoft.VisualStudio.Text.Editor.IWpfTextView View;
    Workspace Workspace;
    DocumentId DocumentId;

    // Replay
    static Dictionary<string, Tuple<ReplayHost, int>> ReplayHosts = new Dictionary<string, Tuple<ReplayHost, int>>();
    ReplayHost ReplayHost;
    VersionStamp ReplayDocumentVersionStamp;

    Document Document => Initialize() ? Workspace.CurrentSolution.GetDocument(DocumentId) : null;
    Project Project => Document?.Project;


    public ReplayAdornment(Microsoft.VisualStudio.Text.Editor.IWpfTextView view)
    {
        var componentModel = (Microsoft.VisualStudio.ComponentModelHost.IComponentModel)Microsoft.VisualStudio.Shell.Package.GetGlobalService(typeof(Microsoft.VisualStudio.ComponentModelHost.SComponentModel));
        Workspace = componentModel.GetService<Microsoft.VisualStudio.LanguageServices.VisualStudioWorkspace>();

        View = view;
        View.LayoutChanged += OnLayoutChanged;
        View.TextBuffer.Changed += OnTextBufferChanged;
        View.ViewportWidthChanged += OnViewportWidthChanged;
        View.Closed += OnClosed;
    }


    bool Initialize()
    {
        if (DocumentId == null)
        {
            var dte = Microsoft.VisualStudio.Shell.Package.GetGlobalService(typeof(EnvDTE.DTE)) as EnvDTE.DTE;
            var activeDocument = dte?.ActiveDocument; // sometimes we're constructed/invoked before ActiveDocument has been set
            if (activeDocument != null) DocumentId = Workspace.CurrentSolution.GetDocumentIdsWithFilePath(activeDocument.FullName).FirstOrDefault();
            if (DocumentId == null) return false;
        }

        var document = Workspace.CurrentSolution.GetDocument(DocumentId);
        var project = document?.Project;
        if (project == null) return false;

        f();
        if (ReplayHost == null)
        {
            lock (ReplayHosts)
            {
                Tuple<ReplayHost, int> t;
                if (ReplayHosts.TryGetValue(project.OutputFilePath, out t)) t = Tuple.Create(t.Item1, t.Item2 + 1);
                else t = Tuple.Create(new ReplayHost(false), 1);
                ReplayHosts[project.OutputFilePath] = t;
                ReplayHost = t.Item1;
            }
            ReplayHost.AdornmentChanged += ReplayHostAdornmentChanged;
            ReplayHost.Erred += ReplayHostErred;
        }

        return true;
    }

    void f()
    {
        BufferBlock<int> x = new BufferBlock<int>();
    }

    private void ReplayHostAdornmentChanged(bool isAdd, int tag, int line, string content, TaskCompletionSource<object> deferral, CancellationToken cancel)
    {
        if (isAdd) Debug.WriteLine($"+A{tag} - ({line}):{content}");
        else Debug.WriteLine($"-A{tag}");

        View.VisualElement.Dispatcher.BeginInvoke((Action)delegate
        {
            //var existing = View.GetAdornmentLayer(nameof(ReplayAdornment)).Elements.FirstOrDefault(e => (int)e.Tag == line) as TextBlock;
            //if (existing != null) { existing.Text = msg; return; }
            //var snapshotLine = View.TextSnapshot.GetLineFromLineNumber(line);
            //var span = new SnapshotSpan(snapshotLine.Start, snapshotLine.End);
            //var geometry = View.TextViewLines.GetMarkerGeometry(span);
            //if (geometry == null) return;
            //var adornment = new TextBlock { Width = 240, Height = geometry.Bounds.Height, Background = Brushes.Yellow, Opacity = 0.2, Text = $"// {msg}" };
            //Canvas.SetLeft(adornment, View.ViewportWidth - adornment.Width);
            //Canvas.SetTop(adornment, geometry.Bounds.Top);
            //View.GetAdornmentLayer(nameof(ReplayAdornment)).AddAdornment(Microsoft.VisualStudio.Text.Editor.AdornmentPositioningBehavior.TextRelative, span, line, adornment, null);
        });
        deferral.SetResult(null);
    }

    private void ReplayHostErred(string error, TaskCompletionSource<object> deferral, CancellationToken cancel)
    {
        Debug.WriteLine(error);
        deferral.SetResult(null);
    }


    async void OnTextBufferChanged(object sender, TextContentChangedEventArgs e)
    {
        if (!Initialize()) return;
        foreach (var change in e.Changes)
        {
            var start = e.Before.GetLineNumberFromPosition(change.OldPosition);
            var count = e.Before.GetLineNumberFromPosition(change.OldPosition + change.OldLength) - start;
            var start2 = e.After.GetLineNumberFromPosition(change.NewPosition);
            var newcount = e.After.GetLineNumberFromPosition(change.NewPosition + change.NewLength) - start2;
            if (start != start2) Debug.WriteLine("oops!");
            await ReplayHost.DocumentHasChangedAsync(Project, Document.FilePath, start, count, newcount);
        }
    }

    async void OnLayoutChanged(object sender, Microsoft.VisualStudio.Text.Editor.TextViewLayoutChangedEventArgs e)
    {
        // Raised whenever the rendered text displayed in the ITextView changes - whenever the view does a layout
        // (which happens when DisplayTextLineContainingBufferPosition is called or in response to text or classification
        // changes), and also when the view scrolls or when its size changes.
        // Responsible for adding the adornment to any reformatted lines.

        if (!Initialize()) return;
        var start = View.TextViewLines.FirstVisibleLine.Start.GetContainingLine().LineNumber;
        var end = View.TextViewLines.LastVisibleLine.End.GetContainingLine().LineNumber;
        await ReplayHost.ViewHasChangedAsync(Document.FilePath, start, end - start + 1);
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

    void OnClosed(object sender, EventArgs e)
    {
        if (ReplayHost == null) return;
        ReplayHost.AdornmentChanged -= ReplayHostAdornmentChanged;
        ReplayHost.Erred -= ReplayHostErred;

        ReplayHost toDispose = null;
        lock (ReplayHosts)
        {
            var t = ReplayHosts[Project.OutputFilePath];
            t = Tuple.Create(t.Item1, t.Item2 - 1);
            if (t.Item2 == 0) { ReplayHosts.Remove(Project.OutputFilePath); toDispose = t.Item1; }
            else { ReplayHosts[Project.OutputFilePath] = t; }
        }
        toDispose?.Dispose();

        ReplayHost = null;
    }




}

