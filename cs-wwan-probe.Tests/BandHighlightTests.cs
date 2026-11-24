using System.Text.RegularExpressions;
using WwanProbe.Application;
using WwanProbe.Domain;
using WwanProbe.Presentation;

namespace WwanProbe.Tests
{
    // LTE band brightness and segmented console lines.
    public class BandHighlightTests
    {
        private static LteNeighbor Cell(string band, long earfcn, long pci) => new LteNeighbor(-100, null, band, earfcn, pci);

        private static ServingCell Serving(string band, long earfcn, long pci) => new ServingCell { Band = band, Earfcn = earfcn, Pci = pci };

        private static FrameLine Segment(FrameLine line, string text) => line.Segments.Single(s => s.Text == text);

        [Fact]
        public void BrightnessDuplicatesMissingObservationsClippingAndPlainOutput()
        {
            RatConfig rat = new RatConfig { LteBands = new[] { 1, 2, 3 }, NrBands = new[] { 78 } };
            List<LteNeighbor> neighbors = new List<LteNeighbor>
            {
                Cell("B2/1900", 600, 0), // the serving cell reported by both sources counts only once
                Cell("B2/1900", 600, 1),
            };
            neighbors.AddRange(Enumerable.Range(0, 5).Select(pci => Cell("B3/1800", 1200, pci)));
            neighbors.Add(Cell("B?/99999", 99999, 1));
            neighbors.Add(null!);
            LteSnapshot snapshot = new LteSnapshot { Serving = new[] { Serving("B2/1900", 600, 0) }, Neighbors = neighbors };
            FrameLine line = MonitorFrame.LteBandLine(rat, snapshot);
            Assert.Equal(" LTE bands: B1 B2 B3   NR bands: n78", line.Text);
            Assert.True(Segment(line, "B1").Color == ConsoleColor.DarkGray && Segment(line, "B1").GrayLevel is null, "Zero cells must keep the default color.");
            Assert.Equal((byte)172, Segment(line, "B2").GrayLevel);
            Assert.Equal((byte)255, Segment(line, "B3").GrayLevel);
            Assert.True(line.Segments[^1].Color == ConsoleColor.DarkGray && line.Segments[^1].GrayLevel is null, "NR suffix must retain its default color.");

            // PCI can repeat on another frequency; zero is a valid EARFCN and PCI.
            snapshot.Serving = new[] { Serving("B1/2100", 0, 0) };
            snapshot.Neighbors = new[] { Cell("B1/2100", 0, 0), Cell("B1/2100", 1, 0), Cell("B1/2100", 1, 1) };
            FrameLine updated = MonitorFrame.LteBandLine(rat, snapshot);
            Assert.Equal((byte)255, Segment(updated, "B1").GrayLevel);
            Assert.Null(Segment(updated, "B3").GrayLevel);
            snapshot.Neighbors = null;
            Assert.Equal((byte)255, Segment(MonitorFrame.LteBandLine(rat, snapshot), "B1").GrayLevel);
            foreach (LteSnapshot? empty in new[] { null, new LteSnapshot() })
            {
                FrameLine plain = MonitorFrame.LteBandLine(rat, empty);
                Assert.All(plain.Segments, s => Assert.True(s.Color == ConsoleColor.DarkGray && s.GrayLevel is null));
            }

            // One and two cells must have a large brightness difference.
            RatConfig smallRat = new RatConfig { LteBands = new[] { 1, 18 } };
            LteSnapshot small = new LteSnapshot
            {
                Serving = new[] { Serving("B1/2100", 0, 0) },
                Neighbors = new[] { Cell("B18/800", 5850, 1), Cell("B18/800", 5850, 2) }.Concat(Enumerable.Range(0, 10).Select(pci => Cell("B3/1800", 1200, pci))).ToList(),
            };
            FrameLine smallLine = MonitorFrame.LteBandLine(smallRat, small);
            Assert.Equal((byte)144, Segment(smallLine, "B1").GrayLevel);
            Assert.Equal((byte)255, Segment(smallLine, "B18").GrayLevel);
            small.Neighbors = new[] { Cell("B18/800", 5850, 1) };
            Assert.Equal(2, MonitorFrame.LteBandLine(smallRat, small).Segments.Count(s => s.GrayLevel == 255));

            // Segmented lines must preserve clipping/padding without counting color escapes.
            foreach (int width in new[] { 0, 13, 15, 80 })
            {
                string expected = width < line.Text.Length ? line.Text.Substring(0, width) : line.Text.PadRight(width);
                string visible = Regex.Replace(ConsoleLine.Render(line, width), @"\x1b\[[0-9;]*m", "");
                Assert.Equal(expected, visible);
            }
            string rendered = ConsoleLine.Render(smallLine, 80);
            Assert.Contains("\e[38;2;144;144;144mB1\e[39m", rendered);
            Assert.Contains("\e[38;2;255;255;255mB18\e[39m", rendered);
            using StringWriter writer = new StringWriter();
            ConsoleRenderer.ShowPlainFrame(new Frame(new List<FrameLine> { line }, new FrameLine()), writer);
            Assert.Equal(line.Text + Environment.NewLine + Environment.NewLine, writer.ToString());
        }

        [Fact]
        public void ConsoleColorsMapToSgrCodes()
        {
            Assert.Equal(37, ConsoleLine.Sgr(ConsoleColor.Gray));
            Assert.Equal(90, ConsoleLine.Sgr(ConsoleColor.DarkGray));
            Assert.Equal(36, ConsoleLine.Sgr(ConsoleColor.DarkCyan));
            Assert.Equal(91, ConsoleLine.Sgr(ConsoleColor.Red));
            Assert.Equal(33, ConsoleLine.Sgr(ConsoleColor.DarkYellow));
            Assert.Equal(97, ConsoleLine.Sgr(ConsoleColor.White));
            Assert.Equal(30, ConsoleLine.Sgr(ConsoleColor.Black));
        }
    }
}
