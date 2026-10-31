using Xarial.XCad.Base.Attributes;
using Xarial.XCad.Features;
using Xarial.XCad.Geometry;
using Xarial.XCad.UI.Commands.Attributes;
using Xarial.XCad.UI.Commands.Enums;

namespace SwAgent
{
    // The ribbon tab, menu and toolbar are built in UI/AgentRibbon.cs; these are the right-click menus.

    /// <summary>Right-click on a face.</summary>
    [ContextMenuCommandGroupInfo(2, typeof(IXFace))]
    public enum FaceContextCommands_e
    {
        [Title("Ask SolidWorks Agent")]
        [Icon(typeof(Icons), nameof(Icons.Selection))]
        AskAgent,
    }

    /// <summary>Right-click on an edge.</summary>
    [ContextMenuCommandGroupInfo(3, typeof(IXEdge))]
    public enum EdgeContextCommands_e
    {
        [Title("Ask SolidWorks Agent")]
        [Icon(typeof(Icons), nameof(Icons.Selection))]
        AskAgent,
    }

    /// <summary>Right-click on a feature in the tree.</summary>
    [ContextMenuCommandGroupInfo(4, typeof(IXFeature))]
    public enum FeatureContextCommands_e
    {
        [Title("Ask SolidWorks Agent")]
        [Icon(typeof(Icons), nameof(Icons.Selection))]
        AskAgent,
    }
}
