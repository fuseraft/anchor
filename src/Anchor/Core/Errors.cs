namespace Anchor.Core;

/// <summary>A tool failure whose message is shown to the model.</summary>
public sealed class ToolException(string message) : Exception(message);

/// <summary>
/// A problem for the user to fix, such as a missing API key or a config file that isn't valid JSON: its message is shown as
/// it is. Any other exception is a bug, and keeps its stack trace.
/// </summary>
public class AnchorException(string message) : Exception(message);
