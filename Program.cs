// LTE Signal Monitor (TUI) for Windows mobile broadband modems (tested: Fibocom L860-GL).
// Usage: cs-wwan-probe [-Interval 0] [-Count 0] [-CsvPath "log.csv"] [-AtPort COM7] [-BatteryInterval 0] [-Gps] [-Nmea]
// Interval=0 means back-to-back sampling (each sample still takes ~1s for counters, and automatic
// samples start at least 1s apart even when one fails at once).
// BatteryInterval>0 waits at least that many seconds after a sample that completed on battery power or
// with Energy Saver on, and the title shows [SAVER <s>s]; 0 (default) ignores the power state.
// Count=0 means infinite loop. CsvPath enables CSV logging (one row per sample, see Application/SnapshotLog.cs).
// AT commands (neighbors, temperature, SINR, CA) go over a vendor MBIM service found automatically;
// AtPort uses that serial AT port instead (see docs/modem-support.md).
// Gps shows Windows satellite fixes. Nmea (implies Gps) also lists satellites from the GNSS
// driver through a helper process elevated with UAC at startup (see docs/gps.md).
// Keys:  q / Esc / Ctrl+C = quit,  p = pause/resume,  r = refresh now,
//        R = reset statistics (history charts, handover history, 2G/3G log),
//        1-6 = show/hide a history chart (RSRP/RSRQ/SNR/RX/TX/Temp),  g = show/hide all charts
//        h = handover history, s = satellite list (-Nmea),
//        Up/Down = chart height, newer/older handovers or scroll satellites
// When stdin/stdout is redirected, falls back to plain sequential output.
//
// Entry point only: parse args, wire dependencies, pick the UI.
// Layers (lower layers never call upper ones):
//   Domain rules <- Infrastructure adapters / Application use cases <- Presentation
using WwanProbe;
using WwanProbe.Application;
using WwanProbe.Infrastructure;
using WwanProbe.Infrastructure.Gnss;
using WwanProbe.Infrastructure.Modem;
using WwanProbe.Presentation;

if (args.Length > 0 && args[0] == NmeaReceiver.HelperArgument) return NmeaHelper.Run([.. args.Skip(1)]);

CommandLine? options = CommandLine.Parse(args, out string? error);
if (options is null)
{
    Console.Error.WriteLine(error);
    Console.Error.WriteLine(CommandLine.Usage);
    return 1;
}
if (options.Help)
{
    Console.WriteLine(CommandLine.Usage);
    return 0;
}

try
{
    WinRtModem? modem = WinRtModem.GetDefault();
    if (modem is null)
    {
        Console.Error.WriteLine("Modem not found");
        return 1;
    }

    MonitorConfig config = new()
    {
        Interval = options.Interval,
        BatteryInterval = options.BatteryInterval,
        Count = options.Count,
        CsvPath = options.CsvPath,
        AtPort = options.AtPort,
    };
    // Kept open for the whole run; a sample abandoned at quit still finishes its read before the query closes.
    using PdhTrafficCounter traffic = new();
    MonitorSession session = MonitorSession.Initialize(modem, traffic, config);
    // Without -BatteryInterval the power state is never read.
    if (options.BatteryInterval > 0) session.Power = new WinRtPowerState();
    try
    {
        if (options.Gps || options.Nmea) session.GpsReceiver = GpsSource.Start();
        // Before the TUI takes the screen: the UAC prompt blocks until it is answered.
        if (options.Nmea) session.NmeaReceiver = NmeaReceiver.Start();
        if (Console.IsInputRedirected || Console.IsOutputRedirected)
        {
            // Ctrl+C ends the loop after the current sample so the receivers below are stopped;
            // a second Ctrl+C terminates at once.
            using CancellationTokenSource cancel = new();
            void onCancel(object? _, ConsoleCancelEventArgs e)
            {
                e.Cancel = !cancel.IsCancellationRequested;
                cancel.Cancel();
            }
            Console.CancelKeyPress += onCancel;
            try { PlainMonitor.Run(session, cancel.Token); }
            finally { Console.CancelKeyPress -= onCancel; }
        }
        else
        {
            TuiMonitor.Run(session);
            Console.WriteLine($"LTE monitor stopped after {session.Iteration} sample(s).");
        }
    }
    finally
    {
        try { session.NmeaReceiver?.Stop(); }
        finally { session.GpsReceiver?.Dispose(); }
    }
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex.Message);
    return 1;
}
