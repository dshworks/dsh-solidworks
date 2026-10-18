using System;
using System.Collections.Generic;

namespace SwBridge;

/// A failure the agent can act on: a stable code, a sentence for the model, optional facts.
/// Codes are the closed list in protocol/tools.json ("errors"); tests hold the two in step.
sealed class BridgeException : Exception
{
    public readonly string Code;
    public readonly Dictionary<string, object> Data2;

    public BridgeException(string code, string message, Dictionary<string, object> data = null) : base(message)
    {
        Code = code;
        Data2 = data;
    }
}
