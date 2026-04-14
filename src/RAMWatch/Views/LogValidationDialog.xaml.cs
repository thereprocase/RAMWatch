using System.Windows;
using RAMWatch.Core.Models;

namespace RAMWatch.Views;

/// <summary>
/// Dialog for logging a stability test result. Collects test tool, duration,
/// coverage/cycle metric, pass/fail, error count, and optional notes,
/// then returns a populated LogValidationMessage for the caller to send.
/// </summary>
public partial class LogValidationDialog : System.Windows.Window
{
    // Set to the populated message when the user clicks Log Result.
    // Null when the dialog is cancelled.
    public LogValidationMessage? Result { get; private set; }

    private static readonly string[] TestTools =
    [
        "Karhu",
        "TM5 Anta777 Extreme",
        "TM5 1usmus",
        "HCI MemTest",
        "OCCT",
        "y-cruncher",
        "Prime95",
        "Other"
    ];

    public LogValidationDialog()
    {
        InitializeComponent();

        TestToolCombo.ItemsSource = TestTools;
        TestToolCombo.SelectedIndex = 0;

        // Update the coverage label when the tool selection changes so the
        // hint text stays contextually accurate.
        TestToolCombo.SelectionChanged += OnToolSelectionChanged;
        UpdateCoverageHint();
    }

    private void OnToolSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        UpdateCoverageHint();
    }

    private void UpdateCoverageHint()
    {
        var tool = TestToolCombo.SelectedItem?.ToString() ?? "";
        CoverageUnitLabel.Text = tool switch
        {
            "Karhu" => "(e.g. 12400%)",
            "TM5 Anta777 Extreme" or "TM5 1usmus" => "(cycles)",
            "HCI MemTest" => "(%)",
            "OCCT" => "(passes)",
            _ => "(optional)"
        };
    }

    private void OnLogResult(object sender, RoutedEventArgs e)
    {
        ValidationErrorText.Text = "";

        if (!TryBuildMessage(out var message, out var error))
        {
            ValidationErrorText.Text = error;
            return;
        }

        Result = message;
        DialogResult = true;
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        Result = null;
        DialogResult = false;
        Close();
    }

    private bool TryBuildMessage(out LogValidationMessage? message, out string error)
    {
        message = null;
        error = "";

        var tool = TestToolCombo.SelectedItem?.ToString() ?? "";
        if (string.IsNullOrWhiteSpace(tool))
        {
            error = "Select a test tool.";
            return false;
        }

        if (!double.TryParse(DurationBox.Text.Trim(), out var durationHours) || durationHours <= 0)
        {
            error = "Enter a valid duration in hours.";
            return false;
        }

        // Coverage is optional — leave metric as 0 when blank.
        double coverageValue = 0;
        string coverageName = "coverage";
        string coverageUnit = "";
        var coverageText = CoverageBox.Text.Trim();
        if (!string.IsNullOrEmpty(coverageText))
        {
            // Strip trailing % if present; store as a plain number with a "%" unit.
            if (coverageText.EndsWith('%'))
            {
                coverageUnit = "%";
                coverageText = coverageText.TrimEnd('%');
            }

            if (!double.TryParse(coverageText, out coverageValue))
            {
                error = "Coverage/cycles must be a number.";
                return false;
            }

            coverageName = tool switch
            {
                "Karhu" => "coverage",
                "TM5 Anta777 Extreme" or "TM5 1usmus" => "cycles",
                "HCI MemTest" => "coverage",
                _ => "metric"
            };
            if (string.IsNullOrEmpty(coverageUnit))
                coverageUnit = tool is "TM5 Anta777 Extreme" or "TM5 1usmus" ? "cycles" : "%";
        }

        if (!int.TryParse(ErrorCountBox.Text.Trim(), out var errorCount) || errorCount < 0)
        {
            error = "Error count must be a non-negative integer.";
            return false;
        }

        var passed = PassRadio.IsChecked == true;
        var notes = NotesBox.Text.Trim();

        message = new LogValidationMessage
        {
            Type = "logValidation",
            RequestId = Guid.NewGuid().ToString("N"),
            TestTool = tool,
            MetricName = coverageName,
            MetricValue = coverageValue,
            MetricUnit = coverageUnit,
            Passed = passed,
            ErrorCount = errorCount,
            DurationMinutes = (int)Math.Round(durationHours * 60),
            Notes = string.IsNullOrEmpty(notes) ? null : notes
        };

        return true;
    }
}
