using System.Windows.Controls;

namespace RAMWatch.Controls;

/// <summary>
/// Three-column legend for the three-axis provenance glyph encoding.
/// Rendered as a compact horizontal strip so it fits into a section
/// header. All content is static; there is no view model binding.
/// </summary>
public partial class DotLegend : UserControl
{
    public DotLegend()
    {
        InitializeComponent();
    }
}
