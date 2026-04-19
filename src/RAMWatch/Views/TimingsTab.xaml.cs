using System.Diagnostics;
using System.Windows;
using System.Windows.Navigation;

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

    private void OnHyperlinkRequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        // Only allow http/https navigation — defence against XAML-level injection
        // of file: or other exotic URI schemes if a future edit pulls the URI
        // from anywhere other than a compile-time constant.
        var uri = e.Uri;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
        {
            e.Handled = true;
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = uri.AbsoluteUri,
                UseShellExecute = true,
            });
        }
        catch
        {
            // Browser launch failures aren't fatal — the user can type the URL manually.
        }

        e.Handled = true;
    }
}
