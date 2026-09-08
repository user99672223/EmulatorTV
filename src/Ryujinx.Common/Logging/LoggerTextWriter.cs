using System;
using System.IO;
using System.Text;

namespace Ryujinx.Common.Logging
{
    /// <summary>
    /// A TextWriter that forwards Console output into <see cref="Logger"/>.
    ///
    /// On Apple mobile targets .NET routes Console through NSLog, which writes both to
    /// stderr and to os_log -- and os_log redacts the dynamic text to &lt;private&gt;.
    /// The redacted copy is NSLog's own, so it cannot be suppressed from managed code;
    /// the only way to make that output readable is to stop it reaching NSLog at all.
    ///
    /// Installing this as Console.Out and Console.Error covers every Console.Write call
    /// in the tree at once, including the ones that matter most: the exception dump in
    /// main_ryujinx_sdl's catch block, which is what a failed game boot prints before
    /// returning -1.
    /// </summary>
    public sealed class LoggerTextWriter : TextWriter
    {
        private readonly StringBuilder _buffer = new();
        private readonly object _lock = new();
        private readonly string _source;

        public LoggerTextWriter(string source)
        {
            _source = source;
        }

        public override Encoding Encoding => Encoding.UTF8;

        public override void Write(char value)
        {
            lock (_lock)
            {
                Append(value);
            }
        }

        public override void Write(string value)
        {
            if (value == null)
            {
                return;
            }

            lock (_lock)
            {
                foreach (char c in value)
                {
                    Append(c);
                }
            }
        }

        public override void WriteLine(string value)
        {
            Write(value);
            Write('\n');
        }

        // Caller holds the lock.
        private void Append(char value)
        {
            if (value == '\n')
            {
                Emit();
            }
            else if (value != '\r')
            {
                _buffer.Append(value);

                // A runaway write should not grow without bound.
                if (_buffer.Length > 16 * 1024)
                {
                    Emit();
                }
            }
        }

        private void Emit()
        {
            if (_buffer.Length == 0)
            {
                return;
            }

            string line = _buffer.ToString();
            _buffer.Clear();

            // Notice is always enabled, so this cannot be filtered out by log-level
            // configuration. The log target must never write back to Console or this
            // would recurse.
            Logger.Notice.Print(LogClass.Application, $"[{_source}] {line}");
        }

        public override void Flush()
        {
            lock (_lock)
            {
                Emit();
            }
        }
    }
}
