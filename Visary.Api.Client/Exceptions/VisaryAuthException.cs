using System;

namespace Visary.Api.Exceptions;

public sealed class VisaryAuthException : Exception
{
    public VisaryAuthException(string message) : base(message) { }
    public VisaryAuthException(string message, Exception inner) : base(message, inner) { }
}
