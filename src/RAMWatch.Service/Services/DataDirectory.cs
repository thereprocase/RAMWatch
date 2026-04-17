using System.Security.AccessControl;
using System.Security.Principal;

namespace RAMWatch.Service.Services;

/// <summary>
/// Manages %ProgramData%\RAMWatch\ — creation and ACL setup.
/// Service owns all writes. Users get read-only access.
/// </summary>
public static class DataDirectory
{
    public static string BasePath { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "RAMWatch");

    public static string LogsPath => Path.Combine(BasePath, "logs");
    public static string SettingsPath => Path.Combine(BasePath, "settings.json");
    public static string SnapshotsPath => Path.Combine(BasePath, "snapshots.json");
    public static string TestsPath => Path.Combine(BasePath, "tests.json");
    public static string LkgPath => Path.Combine(BasePath, "lkg.json");

    // Phase 4 — git-backed tuning history
    public static string HistoryRepoPath => Path.Combine(BasePath, "history");
    public static string GhConfigPath => Path.Combine(BasePath, ".gh");

    /// <summary>
    /// Ensure the data directory exists with correct ACLs.
    /// Called once on service startup.
    /// </summary>
    public static void EnsureCreated()
    {
        Directory.CreateDirectory(BasePath);
        Directory.CreateDirectory(LogsPath);

        try
        {
            SetDirectoryAcl(BasePath);
        }
        catch
        {
            // Non-fatal: ACL setup may fail in non-admin contexts (e.g., testing).
            // The directory still works, just with inherited permissions.
        }
    }

    private static void SetDirectoryAcl(string path)
    {
        var info = new DirectoryInfo(path);
        var security = info.GetAccessControl();

        // Administrators: full control
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));

        // SYSTEM: full control
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));

        // Users: read only (B5: no user-writable paths for the service to act on)
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
            FileSystemRights.ReadAndExecute,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));

        info.SetAccessControl(security);
    }

    /// <summary>
    /// Move a corrupt JSON file out of the way before overwriting it with
    /// defaults. Preserves the original content as
    /// <c>&lt;name&gt;.corrupt.&lt;yyyyMMddHHmmss&gt;&lt;ext&gt;</c> so the user
    /// can recover hand-edited or partially-written state.
    ///
    /// Every journal's Load path used to silently swallow corruption and
    /// reset to defaults — losing validation history, snapshot journal,
    /// drift window, boot-fail entries, etc., with no trace. Archiving
    /// on corruption is the trace.
    ///
    /// Best effort: if the rename fails (locked, permissions), we proceed
    /// to the default-recovery path anyway so the service keeps running.
    /// </summary>
    public static void ArchiveCorruptFile(string path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
        try
        {
            string? dir = Path.GetDirectoryName(path);
            if (string.IsNullOrEmpty(dir)) return;
            string name = Path.GetFileNameWithoutExtension(path);
            string ext = Path.GetExtension(path);
            string archived = Path.Combine(dir,
                $"{name}.corrupt.{DateTime.UtcNow:yyyyMMddHHmmss}{ext}");
            File.Move(path, archived);
        }
        catch
        {
            // Non-fatal — the Load path will still recover to defaults.
        }
    }
}
