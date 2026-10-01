namespace TildeTools.Modules.EmoteSplitter;

// Crosses IPC as an int and C2 and XIM read the numbers, so this shouldn't be renumbered.
public enum SplitTake
{
    // Caller sends it itself.
    NotTaken = 0,

    // Caller sends nothing, may clear its box...
    Queued = 1,

    // Caller sends nothing, keeps the text.
    Refused = 2,
}
