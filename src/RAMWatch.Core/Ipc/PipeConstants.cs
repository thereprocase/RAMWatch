namespace RAMWatch.Core.Ipc;

public static class PipeConstants
{
    /// <summary>
    /// Default pipe name. Multi-instance testing can override via the
    /// <c>RAMWATCH_PIPE_NAME</c> environment variable — both the service
    /// and GUI read <see cref="PipeName"/> which resolves the override at
    /// process start. Must not contain backslashes or whitespace.
    /// </summary>
    public const string DefaultPipeName = "RAMWatch";

    /// <summary>
    /// Effective pipe name for this process. Set from
    /// <c>RAMWATCH_PIPE_NAME</c> when present and non-empty; otherwise
    /// <see cref="DefaultPipeName"/>.
    /// </summary>
    public static readonly string PipeName = ResolvePipeName();

    public const int BufferSize = 65536;
    public const int MaxMessageSize = 1024 * 1024; // 1 MB safety limit

    private static string ResolvePipeName()
    {
        var envOverride = Environment.GetEnvironmentVariable("RAMWATCH_PIPE_NAME");
        if (string.IsNullOrWhiteSpace(envOverride))
            return DefaultPipeName;

        // Reject names with characters that would break
        // \\.\pipe\<name> path composition.
        foreach (var c in envOverride)
        {
            if (char.IsWhiteSpace(c) || c == '\\' || c == '/' || c == ':')
                return DefaultPipeName;
        }
        return envOverride;
    }
}
