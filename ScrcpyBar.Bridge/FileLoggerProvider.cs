using System;
using System.IO;
using Microsoft.Extensions.Logging;

namespace ScrcpyBar.Bridge;

/// <summary>
/// Logs everything to %LOCALAPPDATA%\ScrcpyBar\bridge.log. In windowless mode the console
/// output is invisible, so a file logger is the only observable trace of the app.
/// File is rotated once it grows past 5 MB (previous file becomes bridge.old.log).
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly StreamWriter _writer;

    public FileLoggerProvider()
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ScrcpyBar");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "bridge.log");

            var file = new FileInfo(path);
            if (file.Exists && file.Length > 5 * 1024 * 1024)
            {
                var old = Path.Combine(dir, "bridge.old.log");
                File.Delete(old);
                file.MoveTo(old);
            }

            _writer = new StreamWriter(path, append: true) { AutoFlush = true };
        }
        catch
        {
            // Logging is best effort; a broken log destination must not break the bridge.
            _writer = StreamWriter.Null;
        }
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(_writer, categoryName);

    public void Dispose()
    {
        try
        {
            _writer.Dispose();
        }
        catch
        {
        }
    }

    private sealed class FileLogger(StreamWriter writer, string categoryName) : ILogger
    {
        private readonly string _category = categoryName.Length > 40
            ? categoryName[^40..]
            : categoryName;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            try
            {
                writer.Write(DateTime.Now.ToString("HH:mm:ss.fff", null));
                writer.Write(" [");
                writer.Write(logLevel.ToString());
                writer.Write("] ");
                writer.Write(_category);
                writer.Write(": ");
                writer.WriteLine(formatter(state, exception));
                if (exception != null)
                {
                    writer.WriteLine(exception);
                }
            }
            catch
            {
            }
        }
    }
}
