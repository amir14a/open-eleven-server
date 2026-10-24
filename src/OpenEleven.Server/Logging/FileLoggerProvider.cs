// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 PES5 Indie Team <pes5indie@gmail.com>
//
// This file is part of OpenEleven. See the LICENSE file at the repository root.

using System.Text;
using Microsoft.Extensions.Logging;

namespace OpenEleven.Server.Logging;

/// <summary>
/// Writes the server log to a file, in the same format as the console.
/// </summary>
/// <remarks>
/// Reversing a protocol means a client attempt that lasts two seconds and scrolls off the
/// terminal is data lost, so the file is the durable copy. It is plain text and append-only;
/// when it passes <see cref="MaxBytes"/> it is renamed to <c>.1</c> once and a fresh file is
/// started, which bounds disk use without pulling in a logging framework.
/// </remarks>
public sealed class FileLoggerProvider : ILoggerProvider
{
    /// <summary>Rotate once when the file grows past this. One generation is kept.</summary>
    internal const long MaxBytes = 16L * 1024 * 1024;

    private readonly FileLogWriter _writer;

    public FileLoggerProvider(string path) => _writer = new FileLogWriter(path);

    /// <summary>The absolute path actually written to, for the startup banner.</summary>
    public string Path => _writer.Path;

    /// <summary>False when the path could not be opened; the banner then warns instead.</summary>
    public bool IsOpen => _writer.IsOpen;

    public ILogger CreateLogger(string categoryName) => new FileLogger(categoryName, _writer);

    public void Dispose() => _writer.Dispose();
}

internal sealed class FileLogger(string category, FileLogWriter writer) : ILogger
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None && writer.IsOpen;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel))
            return;

        var message = formatter(state, exception);

        var line = new StringBuilder()
            .Append(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz"))
            .Append(" [").Append(Describe(logLevel)).Append("] ")
            .Append(category).Append(": ")
            .Append(message);

        if (exception is not null)
            line.Append('\n').Append(exception);

        writer.WriteLine(line.ToString());
    }

    private static string Describe(LogLevel level) => level switch
    {
        LogLevel.Trace => "trce",
        LogLevel.Debug => "dbug",
        LogLevel.Information => "info",
        LogLevel.Warning => "warn",
        LogLevel.Error => "fail",
        LogLevel.Critical => "crit",
        _ => level.ToString().ToLowerInvariant(),
    };
}

/// <summary>Serialises appends to one file. Every connection thread can log into it.</summary>
internal sealed class FileLogWriter : IDisposable
{
    private readonly object _gate = new();
    private StreamWriter? _stream;

    public FileLogWriter(string path)
    {
        Path = path;
        _stream = Open(path);
    }

    public string Path { get; }

    public bool IsOpen => _stream is not null;

    public void WriteLine(string line)
    {
        lock (_gate)
        {
            if (_stream is null)
                return;

            try
            {
                if (_stream.BaseStream.Length >= FileLoggerProvider.MaxBytes)
                    Rotate();

                _stream.WriteLine(line);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException
                                           or UnauthorizedAccessException)
            {
                // Drop the line rather than take the process down with it.
            }
        }
    }

    private void Rotate()
    {
        _stream?.Dispose();

        var backup = Path + ".1";
        try
        {
            File.Delete(backup);
            File.Move(Path, backup);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Could not archive the old file; keep logging into it rather than losing output.
        }

        _stream = Open(Path);
    }

    private static StreamWriter? Open(string path)
    {
        try
        {
            var directory = System.IO.Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            return new StreamWriter(
                new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
            {
                AutoFlush = true,
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or ArgumentException or NotSupportedException)
        {
            // A bad log path must not stop the server: the console still works.
            Console.Error.WriteLine($"[warn] file logging disabled: {path}: {ex.Message}");
            return null;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            try { _stream?.Dispose(); } catch (IOException) { }
            _stream = null;
        }
    }
}
