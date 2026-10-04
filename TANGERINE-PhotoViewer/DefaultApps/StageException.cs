using System.IO;
using Microsoft.Win32;
namespace TANGERINE_PhotoViewer.DefaultApps;

internal sealed class StageException : Exception
{
    public string StageCode { get; }
    public StageException(string code, string message) : base(message) => StageCode = code;
    public StageException(string code, string message, Exception inner) : base(message, inner) => StageCode = code;
}


