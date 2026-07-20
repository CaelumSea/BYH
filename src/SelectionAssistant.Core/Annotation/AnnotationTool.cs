namespace SelectionAssistant.Core.Annotation;

/// <summary>
/// R48: annotation tool selection. Number (0) is the R47 numbered badge tool.
/// Rectangle/Ellipse/Arrow/Pen/Highlight are the five new R48 drawing tools.
/// </summary>
public enum AnnotationTool
{
    /// <summary>R47 numbered badge (default on entering annotation mode).</summary>
    Number = 0,

    /// <summary>Rectangle tool: drag to draw a rectangle stroke.</summary>
    Rectangle = 1,

    /// <summary>Ellipse tool: drag to draw an ellipse stroke.</summary>
    Ellipse = 2,

    /// <summary>Arrow tool: drag to draw an arrow line.</summary>
    Arrow = 3,

    /// <summary>Pen tool: drag to draw a freehand path.</summary>
    Pen = 4,

    /// <summary>Highlight tool: drag to draw a semi-transparent highlight path.</summary>
    Highlight = 5,
}
