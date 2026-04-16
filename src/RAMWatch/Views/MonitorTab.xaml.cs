using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using RAMWatch.ViewModels;

namespace RAMWatch.Views;

public partial class MonitorTab : System.Windows.Controls.UserControl
{
    public MonitorTab()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Open the EventDetailDialog for the source whose row was double-clicked.
    /// Walks the visual tree from the click target to the row so a click on a
    /// cell, the row background, or the group header all behave the same.
    /// </summary>
    private void OnErrorRowDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;

        var row = FindParentRow(e.OriginalSource as DependencyObject);
        if (row?.Item is not ErrorSourceVm source) return;

        var events = vm.GetEventsForSource(source.Name);
        var dialog = new EventDetailDialog(source.Name, events)
        {
            Owner = System.Windows.Window.GetWindow(this)
        };
        dialog.ShowDialog();
    }

    private static DataGridRow? FindParentRow(DependencyObject? source)
    {
        while (source is not null && source is not DataGridRow)
        {
            source = System.Windows.Media.VisualTreeHelper.GetParent(source);
        }
        return source as DataGridRow;
    }
}
