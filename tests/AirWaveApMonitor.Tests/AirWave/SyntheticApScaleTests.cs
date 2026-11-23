using System.Diagnostics;
using System.Text;
using AirWaveApMonitor.Core.Contracts;
using AirWaveApMonitor.Infrastructure.AirWave;
using Xunit.Abstractions;

namespace AirWaveApMonitor.Tests.AirWave;

// Synthetic parser stress. Does not claim 5,000 Live APs, full SQLite endurance, or onsite verification.
public sealed class SyntheticApScaleTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(2000)]
    [InlineData(5000)]
    public void RepeatedSyntheticApLists_PreserveCountsAndBoundedXml(int targets)
    {
        const int repetitions=6;
        var watch=Stopwatch.StartNew();
        var parser=new ApListParser(EvidenceSource.Demo);
        for(var cycle=0;cycle<repetitions;cycle++)
        {
            var xml=new StringBuilder(targets*120);
            xml.Append("<amp:amp_ap_list xmlns:amp='http://www.airwave.com' version='1'>");
            int expectedDown=0;
            for(var i=1;i<=targets;i++)
            {
                var down=(i+cycle)%37==0;
                if(down)expectedDown++;
                xml.Append("<ap id='").Append(i).Append("'><device_category>thin_ap</device_category><is_up>")
                    .Append(down?"false":"true")
                    .Append("</is_up><name>Demo</name></ap>");
            }
            xml.Append("</amp:amp_ap_list>");
            using var stream=new MemoryStream(Encoding.UTF8.GetBytes(xml.ToString()));
            var batch=parser.Parse(stream,DateTimeOffset.UtcNow);
            Assert.Equal(RunOutcome.Succeeded,batch.Outcome);
            Assert.Equal(targets,batch.Observations.Length);
            Assert.Empty(batch.ExcludedRecords);
            Assert.Equal(expectedDown,batch.Observations.Count(x=>x.State==ApState.Down));
            Assert.Equal(targets-expectedDown,batch.Observations.Count(x=>x.State==ApState.Up));
        }
        watch.Stop();
        output.WriteLine($"synthetic-ap-parse targets={targets}; cycles={repetitions}; elapsedMs={watch.ElapsedMilliseconds}; allocatedBytes={GC.GetTotalAllocatedBytes(false)}; liveApi=false");
    }
}
