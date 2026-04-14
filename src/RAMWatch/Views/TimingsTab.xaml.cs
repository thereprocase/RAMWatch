using System.Windows;

namespace RAMWatch.Views;

public partial class TimingsTab : System.Windows.Controls.UserControl
{
    public TimingsTab()
    {
        InitializeComponent();
    }

    private void OnSaveSnapshot(object sender, RoutedEventArgs e)
    {
        // Delegate to MainWindow so the naming dialog can be shown from the View layer.
        // Walking up to Window is safe here — TimingsTab is always hosted in MainWindow.
        if (Window.GetWindow(this) is MainWindow mainWindow)
            mainWindow.ShowSnapshotDialogAndSave();
    }
}
