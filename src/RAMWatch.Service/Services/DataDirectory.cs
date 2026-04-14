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
}
