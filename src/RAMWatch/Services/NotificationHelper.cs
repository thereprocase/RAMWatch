using System.Diagnostics;
using System.Text;

namespace RAMWatch.Services;

/// <summary>
/// Sends Windows toast notifications via a PowerShell subprocess.
///
/// Using PowerShell keeps the GUI binary free of WinRT COM registration requirements.
/// The WinRT ToastNotificationManager requires the calling app to have an AppUserModelID
/// registered in the Start menu; the PowerShell workaround sidesteps that by launching
/// a subprocess that runs as a shell application with the necessary registration.
///
/// Fallback: msg.exe (Windows built-in, displays a dialog box to the current user's session).
///
/// Rate limiting and toggle checks are the caller's responsibility (MainViewModel.MaybeSendNotification).
/// </summary>
public static class NotificationHelper
{
    /// <summary>
    /// Send a Windows toast notification asynchronously.
    /// Fires and forgets a PowerShell subprocess — does not block the caller.
    /// Silently discards any failure; notifications are non-critical.
    /// </summary>
    public static void SendToast(string title, string body)
    {
        // Sanitise inputs before embedding in the PowerShell command.
        string safeTitle = SanitiseForPowerShell(title);
        string safeBody  = SanitiseForPowerShell(body);

        // Build the PowerShell script without interpolated raw strings so the
        // C# compiler does not misparse PS-style $variables as C# interpolation holes.
        // Single-quoted PS strings are used for the notification text values
        // (safe because backticks and single quotes were removed by SanitiseForPowerShell).
        var sb = new StringBuilder();
        sb.AppendLine("try {");
        sb.AppendLine("  [Windows.UI.Notifications.ToastNotificationManager, Windows.UI.Notifications, ContentType = WindowsRuntime] | Out-Null");
        sb.AppendLine("  $template = [Windows.UI.Notifications.ToastTemplateType]::ToastText02");
        sb.AppendLine("  $xml = [Windows.UI.Notifications.ToastNotificationManager]::GetTemplateContent($template)");
        sb.AppendLine("  $nodes = $xml.GetElementsByTagName('text')");
        sb.AppendLine("  $nodes[0].AppendChild($xml.CreateTextNode('" + safeTitle + "')) | Out-Null");
        sb.AppendLine("  $nodes[1].AppendChild($xml.CreateTextNode('" + safeBody  + "')) | Out-Null");
        sb.AppendLine("  $toast = [Windows.UI.Notifications.ToastNotification]::new($xml)");
        sb.AppendLine("  [Windows.UI.Notifications.ToastNotificationManager]::CreateToastNotifier('RAMWatch').Show($toast)");
        sb.AppendLine("} catch {");
        sb.AppendLine("  try { msg.exe * /time:10 '" + safeTitle + ": " + safeBody + "' } catch { }");
        sb.AppendLine("}");

        string script = sb.ToString();

        try
        {
            var psi = new ProcessStartInfo("powershell.exe")
            {
                RedirectStandardOutput = false,
                RedirectStandardError  = false,
                UseShellExecute        = false,
                CreateNoWindow         = true,
            };
            psi.ArgumentList.Add("-NoProfile");
            psi.ArgumentList.Add("-NonInteractive");
            psi.ArgumentList.Add("-Command");
            psi.ArgumentList.Add(script);

            // Fire and forget — we do not wait for the process.
            // Notification delivery is best-effort; any failure is silent.
            Process.Start(psi);
        }
        catch
        {
            // Notification failure is never fatal.
        }
    }

    /// <summary>
    /// Strip characters that would break the PowerShell single-quoted string.
    /// Single quotes would close the PS string; backticks are the PS escape character.
    /// Newlines are flattened to spaces. Input is truncated to 200 chars.
    /// </summary>
    private static string SanitiseForPowerShell(string input)
    {
        if (string.IsNullOrEmpty(input)) return "";
        if (input.Length > 200) input = input[..200];
        return input
            .Replace("'",  "")
            .Replace("`",  "")
            .Replace("\n", " ")
            .Replace("\r", "");
    }
}
