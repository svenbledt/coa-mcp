namespace CoaMcp.Pipeline.Knowledge;

/// <summary>A problem the owner can act on: bad input, a refused overwrite, a failed copy. The message never holds entry text.</summary>
public sealed class KnowledgeException : Exception
{
    public KnowledgeException(string message, Exception? inner = null) : base(message, inner) { }
}
