namespace RAMWatch.Core.Ipc;

public static class PipeConstants
{
    public const string PipeName = "RAMWatch";
    public const int BufferSize = 65536;
    public const int MaxMessageSize = 1024 * 1024; // 1 MB safety limit
}
