using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using RAMWatch.ViewModels;

namespace RAMWatch.Views;

public partial class SettingsTab : UserControl
{
    public SettingsTab()
    {
        InitializeComponent();
    }

    private void OnBrowseMirrorDirectory(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Select mirror directory",
            Multiselect = false
        };

        if (dialog.ShowDialog() != true) return;

        // Update the TextBox and push the value through the binding
        MirrorDirectoryBox.Text = dialog.FolderName;

        // If the DataContext is a SettingsViewModel, assign directly so the
        // binding target (TextBox) doesn't lose focus before committing.
        if (DataContext is SettingsViewModel vm)
            vm.MirrorDirectory = dialog.FolderName;
    }
}
