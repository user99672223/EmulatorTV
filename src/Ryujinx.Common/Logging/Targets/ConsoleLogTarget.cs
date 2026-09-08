using Ryujinx.Common.Logging.Formatters;
using System;
using System.Runtime.InteropServices;

namespace Ryujinx.Common.Logging.Targets
{
    public class ConsoleLogTarget : ILogTarget
    {
        private readonly ILogFormatter _formatter;

        private readonly string _name;

        string ILogTarget.Name { get => _name; }

        private static ConsoleColor GetLogColor(LogLevel level) => level switch
        {
            LogLevel.Info => ConsoleColor.White,
            LogLevel.Warning => ConsoleColor.Yellow,
            LogLevel.Error => ConsoleColor.Red,
            LogLevel.Stub => ConsoleColor.DarkGray,
            LogLevel.Notice => ConsoleColor.Cyan,
            LogLevel.Trace => ConsoleColor.DarkCyan,
            _ => ConsoleColor.Gray,
        };

        public ConsoleLogTarget(string name)
        {
            _formatter = new DefaultLogFormatter();
            _name = name;
        }

        // On tvOS the process's stdout reaches the unified log, but os_log treats
        // the dynamic text as private and prints <private>, which hides the very
        // message a crash needs. RyujinxHelper re-emits it with a {public}
        // specifier so it stays readable.
        [DllImport("RyujinxHelper.framework/RyujinxHelper", EntryPoint = "MeloLogPublic", CallingConvention = CallingConvention.Cdecl)]
        private static extern void MeloLogPublic([MarshalAs(UnmanagedType.LPUTF8Str)] string message);

        private static bool _publicLogUnavailable;

        private static void LogPublic(string line)
        {
            if (_publicLogUnavailable)
            {
                return;
            }

            try
            {
                MeloLogPublic(line);
            }
            catch (Exception)
            {
                // Older builds of the helper do not export it; stop trying.
                _publicLogUnavailable = true;
            }
        }

        public void Log(object sender, LogEventArgs args)
        {
            if ((OperatingSystem.IsIOS() || OperatingSystem.IsTvOS()))
            {
                string line = _formatter.Format(args);

                if (OperatingSystem.IsTvOS())
                {
                    LogPublic(line);
                }

                Console.WriteLine(line);
            }
            else
            {
                Console.ForegroundColor = GetLogColor(args.Level);
                Console.WriteLine(_formatter.Format(args));
                Console.ResetColor();
            }
        }

        public void Dispose()
        {
            GC.SuppressFinalize(this);

            if (!(OperatingSystem.IsIOS() || OperatingSystem.IsTvOS()))
            {
                Console.ResetColor();
            }
        }
    }
}
