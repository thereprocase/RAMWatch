using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using RAMWatch.ViewModels;

namespace RAMWatch.Views;

public partial class SettingsTab : UserControl
{
    private static readonly Regex NonDigit = new("[^0-9]", RegexOptions.Compiled);
    // Valid BIOS layout choices presented in the dropdown.
    // Order matches the vendor enum: Auto first, then alphabetical, Default last.
    private static readonly string[] BiosLayoutChoices =
        ["Auto", "MSI", "ASUS", "Gigabyte", "ASRock", "Default"];

    /// <summary>
    /// Static list of designation choices for binding from the ItemTemplate
    /// (x:Static requires a public static member on the code-behind class).
    /// </summary>
    public static IReadOnlyList<string> DesignationChoicesList { get; } =
        SettingsViewModel.DesignationChoices;

    public SettingsTab()
    {
        InitializeComponent();
        BiosLayoutComboBox.ItemsSource = BiosLayoutChoices;
    }

    /// <summary>
    /// Reject non-digit characters in numeric TextBox fields.
    /// </summary>
    private void NumericOnly_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        e.Handled = NonDigit.IsMatch(e.Text);
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
