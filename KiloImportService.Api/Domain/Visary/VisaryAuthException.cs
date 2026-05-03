using System;

namespace KiloImportService.Api.Domain.Visary;

public sealed class VisaryAuthException : Exception
{
    public VisaryAuthException(string message) : base(message) { }
}
