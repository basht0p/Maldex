namespace Maldex.Agent;

/// <summary>Opens the agent's channel port for full-duplex use.</summary>
public static class AgentPort
{
    /// <summary>
    /// Opens <paramref name="path"/> read-write for overlapped I/O with no FileStream buffer. The agent keeps a read pending
    /// for host commands while it writes telemetry; a buffered FileStream serialises async reads and writes on one semaphore,
    /// so every write would wait for the next command.
    /// </summary>
    public static FileStream OpenDuplex(string path) =>
        new(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite, bufferSize: 0, FileOptions.Asynchronous);
}
