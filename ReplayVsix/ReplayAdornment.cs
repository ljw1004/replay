using System;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Formatting;
using System.Windows;
using System.Diagnostics;
using System.ComponentModel.Composition;
using Microsoft.VisualStudio.Utilities;
using System.Linq;

// 
// 
// 
// 
// Microsoft.VSSDK.BuildTools

[Export(typeof(IWpfTextViewCreationListener)), ContentType("text"), TextViewRole(PredefinedTextViewRoles.Document)]
public sealed class ReplayAdornmentTextViewCreationListener : IWpfTextViewCreationListener
{
    public void TextViewCreated(IWpfTextView textView) => new ReplayAdornment(textView);

#pragma warning disable 649, 169
    [Export(typeof(AdornmentLayerDefinition)), Name("ReplayAdornment"), Order(After = PredefinedAdornmentLayers.Selection, Before = PredefinedAdornmentLayers.Text)]
    private AdornmentLayerDefinition editorAdornmentLayer;
#pragma warning restore 649,146
}


public sealed class ReplayAdornment
{
    private readonly IAdornmentLayer Layer;
    private readonly IWpfTextView View;
    private readonly Brush Brush = new SolidColorBrush(Color.FromArgb(0x20, 0x00, 0x00, 0xff)).AndFreeze();
    private readonly Pen Pen = new Pen(new SolidColorBrush(Colors.Red).AndFreeze(), 0.5).AndFreeze();
    //private Microsoft.CodeAnalysis.Workspace _workspace;
    //private Microsoft.CodeAnalysis.DocumentId _documentId;

    public ReplayAdornment(IWpfTextView view)
    {
        Layer = view.GetAdornmentLayer("ReplayAdornment");
        View = view;
        View.LayoutChanged += OnLayoutChanged;
    }

    internal void OnLayoutChanged(object sender, TextViewLayoutChangedEventArgs e)
    {
        // Raised whenever the rendered text displayed in the ITextView changes - whenever the view does a layout
        // (which happens when DisplayTextLineContainingBufferPosition is called or in response to text or classification
        // changes), and also when the view scrolls horizontally, or when its size changes.
        // Responsible for adding the adornment to any reformatted lines.
        try
        {
            var b = false;
            if (b)
            {
                //var componentModel = (Microsoft.VisualStudio.ComponentModelHost.IComponentModel)Microsoft.VisualStudio.Shell.Package.GetGlobalService(typeof(Microsoft.VisualStudio.ComponentModelHost.SComponentModel));
                //var workspace = componentModel.GetService<Microsoft.VisualStudio.LanguageServices.VisualStudioWorkspace>();
                //var dte = Microsoft.VisualStudio.Shell.Package.GetGlobalService(typeof(EnvDTE.DTE)) as EnvDTE.DTE;
                //var activeDocument = dte?.ActiveDocument;
                //if (activeDocument != null)
                //{
                //    var documentId = workspace.CurrentSolution.GetDocumentIdsWithFilePath(activeDocument.FullName).FirstOrDefault();
                //    if (documentId != null)
                //    {
                //        var document = workspace.CurrentSolution.GetDocument(documentId);
                //        var project = document.Project;
                //        System.Diagnostics.Debug.WriteLine(project.OutputFilePath);
                //    }
                //}
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex.Message);
        }

        foreach (ITextViewLine line in e.NewOrReformattedLines)
        {
            CreateVisuals(line);
        }
    }

    private void CreateVisuals(ITextViewLine line)
    {
        IWpfTextViewLineCollection textViewLines = View.TextViewLines;
        for (int charIndex = line.Start; charIndex < line.End; charIndex++)
        {
            if (View.TextSnapshot[charIndex] == 'a')
            {
                SnapshotSpan span = new SnapshotSpan(View.TextSnapshot, Span.FromBounds(charIndex, charIndex + 1));
                Geometry geometry = textViewLines.GetMarkerGeometry(span);
                if (geometry != null)
                {
                    var drawing = new GeometryDrawing(Brush, Pen, geometry).AndFreeze();
                    var image = new Image { Source = new DrawingImage(drawing).AndFreeze() };
                    Canvas.SetLeft(image, geometry.Bounds.Left);
                    Canvas.SetTop(image, geometry.Bounds.Top);
                    Layer.AddAdornment(AdornmentPositioningBehavior.TextRelative, span, null, image, null);
                }
            }
        }
    }

    //public Microsoft.CodeAnalysis.Workspace Workspace
    //{
    //    get
    //    {
    //        if (_workspace != null) return _workspace;
    //        var componentModel = (Microsoft.VisualStudio.ComponentModelHost.IComponentModel)Microsoft.VisualStudio.Shell.Package.GetGlobalService(typeof(Microsoft.VisualStudio.ComponentModelHost.SComponentModel));
    //        _workspace = componentModel.GetService<Microsoft.VisualStudio.LanguageServices.VisualStudioWorkspace>();
    //        return _workspace;
    //    }
    //}

    //public Microsoft.CodeAnalysis.DocumentId DocumentId
    //{
    //    get
    //    {
    //        if (_documentId != null) return _documentId;
    //        //var dte = Microsoft.VisualStudio.Shell.Package.GetGlobalService(typeof(EnvDTE.DTE)) as EnvDTE.DTE;
    //        //var activeDocument = dte?.ActiveDocument;
    //        //if (activeDocument == null) return _documentId;
    //        //_documentId = Workspace.CurrentSolution.GetDocumentIdsWithFilePath(activeDocument.FullName).FirstOrDefault();
    //        return _documentId;
    //        //var d = workspace.CurrentSolution.GetDocument(documentid);
    //    }
    //}

}


public static class Extensions
{
    public static T AndFreeze<T>(this T freezable) where T : Freezable
    {
        freezable.Freeze(); return freezable;
    }
}
