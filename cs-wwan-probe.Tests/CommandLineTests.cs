namespace WwanProbe.Tests
{
    // Option binding rules (CommandLine).
    public class CommandLineTests
    {
        private static CommandLine Parse(params string[] args)
        {
            CommandLine? options = CommandLine.Parse(args, out string? error);
            Assert.True(options is not null, error);
            return options!;
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
            Assert.Contains("Nmae", Fail("-Nmae"));
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
            Assert.True(o.Interval == 2 && o.Count == 3 && o.CsvPath == "x.csv" && o.AtPort == "");
            o = Parse("-Count", "4", "7");
            Assert.True(o.Count == 4 && o.Interval == 7);
            Assert.True(Parse("-?").Help);
            Assert.True(Parse().Interval == 0 && Parse().Count == 0);
        }

        [Fact]
        public void InvalidValuesFail()
        {
            Assert.Contains("ambiguous", Fail("-C", "5"));
            Assert.Contains("Interval", Fail("-Interval", "86401"));
            Assert.Contains("Interval", Fail("-Interval", "-1"));
            Assert.Contains("Count", Fail("-Count", "x"));
            Assert.Contains("AtPort", Fail("-AtPort", "USB1"));
            Assert.Contains("Missing an argument", Fail("-CsvPath"));
            Assert.Contains("more than once", Fail("-Gps", "-Gps"));
            Assert.Contains("positional", Fail("1", "2", "a.csv", "COM1", "extra"));
        }
    }
}
