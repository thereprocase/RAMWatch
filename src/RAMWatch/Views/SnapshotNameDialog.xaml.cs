using System.Windows;

namespace RAMWatch.Views;

/// <summary>
/// Modal dialog that asks the user for a snapshot name before saving.
/// Call ShowDialog(); if it returns true, read Label for the user's input.
/// </summary>
public partial class SnapshotNameDialog : System.Windows.Window
{
    /// <summary>
    /// The label entered by the user. Non-null and non-empty only when
    /// ShowDialog() returns true.
    /// </summary>
    public string? Label { get; private set; }

    public SnapshotNameDialog(string defaultLabel)
    {
        InitializeComponent();
        NameBox.Text = defaultLabel;

        // Select all so the user can type straight over the default without
        // needing to clear it first. Cursor ends up at the end if they don't.
        Loaded += (_, _) =>
        {
            NameBox.Focus();
            NameBox.SelectAll();
        };
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        var text = NameBox.Text.Trim();
        if (string.IsNullOrEmpty(text))
            text = null;

        Label = text;
        DialogResult = true;
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        Label = null;
        DialogResult = false;
        Close();
    }
}
