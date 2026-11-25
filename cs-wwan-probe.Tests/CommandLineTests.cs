using System.Reflection;

namespace WwanProbe.Tests
{
    // Option binding rules (CommandLine).
    public class CommandLineTests
    {
        private static CommandLine Parse(params string[] args)
        {
            CommandLine? options = CommandLine.Parse(args, out string? error);
            Assert.True(options is not null, error);
            return options;
        }

        private static string Fail(params string[] args)
        {
            Assert.Null(CommandLine.Parse(args, out string? error));
            return error!;
        }

        // A misspelled option must fail instead of silently running without NMEA.
        [Fact]
        public void UnknownOptionsAreRejectedAndNemaSelectsNmea()
        {
            Assert.Contains("Nmae", Fail("-Nmae"), StringComparison.Ordinal);
            Assert.True(Parse("-Nema").Nmea);
            Assert.True(Parse("-nmea").Nmea);
            Assert.False(Parse("-Gps").Nmea);
        }

        [Fact]
        public void NamesPrefixesPositionsAndValues()
        {
            CommandLine o = Parse("-Interval", "5", "-Count:10", "-csv", "log.csv", "-AtPort", "com7", "-G", "-Nmea:$false");
            Assert.True(o.Interval == 5 && o.Count == 10 && o.CsvPath == "log.csv" && o.AtPort == "com7" && o.Gps && !o.Nmea);
            o = Parse("2", "3", "x.csv");
            Assert.True(o.Interval == 2 && o.Count == 3 && o.CsvPath == "x.csv" && string.IsNullOrEmpty(o.AtPort));
            o = Parse("-Count", "4", "7");
            Assert.True(o.Count == 4 && o.Interval == 7);
            Assert.True(Parse("-?").Help);
            Assert.True(Parse().Interval == 0 && Parse().Count == 0);
        }

        [Fact]
        public void InvalidValuesFail()
        {
            Assert.Contains("ambiguous", Fail("-C", "5"), StringComparison.Ordinal);
            Assert.Contains("Interval", Fail("-Interval", "86401"), StringComparison.Ordinal);
            Assert.Contains("Interval", Fail("-Interval", "-1"), StringComparison.Ordinal);
            Assert.Contains("Count", Fail("-Count", "x"), StringComparison.Ordinal);
            Assert.Contains("AtPort", Fail("-AtPort", "USB1"), StringComparison.Ordinal);
            Assert.Contains("Missing an argument", Fail("-CsvPath"), StringComparison.Ordinal);
            Assert.Contains("more than once", Fail("-Gps", "-Gps"), StringComparison.Ordinal);
            Assert.Contains("positional", Fail("1", "2", "a.csv", "COM1", "extra"), StringComparison.Ordinal);
        }

        // -BatteryInterval binds by name only (any unambiguous prefix, -B included) and leaves the
        // positions of Interval / Count / CsvPath / AtPort and the existing prefixes as they were.
        [Fact]
        public void BatteryIntervalIsNamedOnly()
        {
            Assert.Equal(0, Parse().BatteryInterval);
            Assert.Equal(5, Parse("-BatteryInterval", "5").BatteryInterval);
            CommandLine o = Parse("-b:30", "2", "3", "x.csv", "COM1");
            Assert.True(o.BatteryInterval == 30 && o.Interval == 2 && o.Count == 3 && o.CsvPath == "x.csv" && o.AtPort == "COM1");
            o = Parse("4", "-BATTERYINT", "86400");
            Assert.True(o.Interval == 4 && o.BatteryInterval == 86400);
            Assert.Equal(0, Parse("-batteryinterval:0").BatteryInterval);
            o = Parse("-I", "7");
            Assert.True(o.Interval == 7 && o.BatteryInterval == 0);
            Assert.Contains("positional", Fail("1", "2", "a.csv", "COM1", "5"), StringComparison.Ordinal);
            Assert.Contains("more than once", Fail("-B", "1", "-BatteryInterval", "2"), StringComparison.Ordinal);
        }

        [Fact]
        public void BatteryIntervalOutOfRangeFailsWithExitCode1()
        {
            Assert.Contains("BatteryInterval", Fail("-BatteryInterval", "86401"), StringComparison.Ordinal);
            Assert.Contains("BatteryInterval", Fail("-BatteryInterval", "-1"), StringComparison.Ordinal);
            Assert.Contains("BatteryInterval", Fail("-B", "x"), StringComparison.Ordinal);
            Assert.Contains("Missing an argument", Fail("-BatteryInterval"), StringComparison.Ordinal);
            // The program prints the error and the usage, and ends with 1 before it looks for a modem.
            Assert.Equal(1, RunProgram("-BatteryInterval", "86401"));
            Assert.Equal(1, RunProgram("-B", "-1"));
        }

        // The program's entry point (top-level statements) with these arguments; returns the exit code.
        private static object? RunProgram(params string[] args)
        {
            MethodInfo main = typeof(CommandLine).Assembly.EntryPoint!;
            return main.Invoke(null, [args]);
        }
    }
}
