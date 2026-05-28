using System.Collections.ObjectModel;

namespace MDViewer.Models;

public class HeadingNode
{
    public string Title { get; set; } = string.Empty;

    public int Level { get; set; }

    public int LineNumber { get; set; }

    public int CharacterOffset { get; set; }

    public int RenderOccurrence { get; set; }

    public ObservableCollection<HeadingNode> Children { get; } = [];
}
