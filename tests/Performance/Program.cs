using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AirWaveApMonitor.Core.Contracts;
using AirWaveApMonitor.Infrastructure.AirWave;
using AirWaveApMonitor.Infrastructure.Maintenance;
using AirWaveApMonitor.Infrastructure.Storage;

// Standalone synthetic measurement tool; never contacts an AirWave server.
// One fresh process and fresh folder per count make process memory scope explicit.
if (args.Length != 2 || !int.TryParse(args[0], out var count) || count is < 1 or > 5000)
    throw new ArgumentException("Usage: dotnet run --project tests/Performance -c Release -- <count:1..5000> <new-output-directory>");
var output = Path.GetFullPath(args[1]);
if (Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any())
    throw new IOException("Measurement output directory must be empty; existing data is preserved.");
Directory.CreateDirectory(output);
var sourceCommit = Environment.GetEnvironmentVariable("AIRWAVE_MEASUREMENT_COMMIT");
var location = StoragePaths.Open(AppMode.Demo, Path.Combine(output, "synthetic-data"));
using var writeGate = new SemaphoreSlim(1, 1);
var clock = new MeasurementClock();
var store = new SqliteCollectionStore(location, writeGate, clock);
var profile = new ProfileSnapshot(Guid.NewGuid(), location.DatasetId, new("https://synthetic.example.invalid/"),
    1, 1, Enumerable.Range(1, count).Select(i => new ExpectedTarget(i.ToString(CultureInfo.InvariantCulture))),
    BaselineStatus.Confirmed, new());
var parser = new ApListParser(EvidenceSource.Demo);
var records = new List<object>();
var total = Stopwatch.StartNew();
var initialization = Stopwatch.StartNew();
await store.InitializeAsync(default);
initialization.Stop();
var beforeBytes = DatabaseBytes(location);
long? initialDownRun = null;
long lastRun = 0;
int openIncidents = 0;
// Missing is represented by every row omitting is_up, not by a numeric state or fake Down.
var frames = new (string Name, bool? Up)[]
{
    ("baseline-all-up", true), ("all-down", false), ("all-state-unknown", null),
    ("repeat-down-after-gap", false), ("all-recovered", true),
    ("repeat-change-down-1", false), ("repeat-change-up-1", true),
    ("repeat-change-down-2", false), ("repeat-change-up-2", true)
};
foreach (var (name, up) in frames)
{
    var frameBytesBefore = DatabaseBytes(location);
    var xml = CreateXml(count, up);
    var allocatedBefore = GC.GetTotalAllocatedBytes();
    var frame = Stopwatch.StartNew();
    var begin = Stopwatch.StartNew();
    var context = await store.BeginRunAsync(profile, CollectionKind.ApStatus, RunTrigger.Manual, default);
    begin.Stop();
    var parse = Stopwatch.StartNew();
    using var input = new MemoryStream(xml, writable: false);
    var batch = parser.Parse(input, clock.GetUtcNow());
    parse.Stop();
    Require(batch.Observations.Length == count && batch.ExcludedRecords.Length == 0, "parser target count");
    var apply = Stopwatch.StartNew();
    var receipt = await store.ApplyApBatchAsync(context, batch, default);
    apply.Stop();
    var query = Stopwatch.StartNew();
    var view = await store.GetDashboardAsync(new(location.DatasetId, 1, context.RunId, PageSize: 500), default);
    query.Stop();
    frame.Stop();
    var frameAllocated = GC.GetTotalAllocatedBytes() - allocatedBefore;
    Require(view.TotalRows == count && view.Summary.TargetCount == count, "stored target count");
    Require(view.Rows.Length == Math.Min(500, count), "first page row count");
    Require(view.Summary == receipt.Summary, "same run summary");
    if (up is null)
    {
        Require(receipt.Outcome == RunOutcome.Failed && view.Summary.ConfirmedUp is null
            && view.Summary.ConfirmedDown is null && view.Summary.Unknown == count, "all unknown remains unknown");
        Require(view.Summary.OpenIncidents == openIncidents, "failure preserves incidents");
    }
    else
    {
        Require(receipt.Outcome == RunOutcome.Succeeded, "valid collection outcome");
        Require(view.Summary.ConfirmedUp == (up.Value ? count : 0)
            && view.Summary.ConfirmedDown == (up.Value ? 0 : count) && view.Summary.Unknown == 0, "valid aggregate");
        openIncidents = up.Value ? 0 : count;
        Require(view.Summary.OpenIncidents == openIncidents, "incident aggregate");
    }
    if (name == "all-down") { initialDownRun = context.RunId; Require(view.Summary.NewDown == count, "new incidents"); }
    if (name == "repeat-down-after-gap") Require(view.Summary.ExistingDown == count && view.Summary.NewDown == 0, "no repeated incident");
    if (name == "all-recovered") Require(view.Summary.Recovered == count, "one recovery per AP");
    // Check every page against the pinned run. Keep this verification time outside
    // the measured parse/begin/commit/first-page frame and report it separately.
    var listVerification = Stopwatch.StartNew();
    var seen = new HashSet<string>(StringComparer.Ordinal);
    CheckRows(view);
    for (var page = 2; page <= (count + 499) / 500; page++)
        CheckRows(await store.GetDashboardAsync(new(location.DatasetId, 1, context.RunId, Page: page, PageSize: 500), default));
    Require(seen.Count == count, "complete distinct AP list");
    listVerification.Stop();
    lastRun = context.RunId;
    records.Add(new
    {
        name, context.RunId, xmlBytes = xml.Length, parseMilliseconds = parse.Elapsed.TotalMilliseconds,
        beginMilliseconds = begin.Elapsed.TotalMilliseconds, applyAggregateAndStoreMilliseconds = apply.Elapsed.TotalMilliseconds,
        first500QueryMilliseconds = query.Elapsed.TotalMilliseconds, totalFrameMilliseconds = frame.Elapsed.TotalMilliseconds,
        allocatedBytes = frameAllocated, completeListVerificationMilliseconds = listVerification.Elapsed.TotalMilliseconds,
        verifiedListRows = seen.Count,
        databaseBytes = DatabaseBytes(location), databaseGrowthBytes = DatabaseBytes(location) - frameBytesBefore,
        processMemory = Memory(), summary = view.Summary, outcome = receipt.Outcome.ToString()
    });
    clock.Now += TimeSpan.FromMinutes(5);

    void CheckRows(DashboardSnapshot page)
    {
        Require(page.RunId == context.RunId && page.TotalRows == count, "pinned page scope");
        foreach (var row in page.Rows)
        {
            Require(seen.Add(row.SourceApId), "no duplicated list targets");
            Require(int.TryParse(row.SourceApId, out var id) && id >= 1 && id <= count, "expected list target");
            Require(row.CurrentState == (up is null ? (ApState?)null : up.Value ? ApState.Up : ApState.Down), "list state matches aggregate");
        }
    }
}

var reopened = new SqliteCollectionStore(location, writeGate, clock);
var reopen = Stopwatch.StartNew();
await reopened.InitializeAsync(default);
var historical = await reopened.GetDashboardAsync(new(location.DatasetId, 1, initialDownRun), default);
var latest = await reopened.GetDashboardAsync(new(location.DatasetId, 1, lastRun), default);
reopen.Stop();
Require(historical.Summary.ConfirmedDown == count && historical.Summary.OpenIncidents == count, "historical all down immutable");
Require(latest.Summary.ConfirmedUp == count && latest.Summary.OpenIncidents == 0, "reopen latest unchanged");
var backupTimer = Stopwatch.StartNew();
var backup = await new BackupService(location, writeGate, clock).CreateAsync(BackupKind.PreUpdate, default);
backupTimer.Stop();
var retentionTimer = Stopwatch.StartNew();
var retention = await new RetentionService(location, writeGate, reopened).PruneAsync(clock.Now.AddDays(91), default);
retentionTimer.Stop();
var afterRetention = await reopened.GetDashboardAsync(new(location.DatasetId, 1, initialDownRun), default);
Require(afterRetention.Summary == historical.Summary, "retention preserves historical aggregates");
total.Stop();
var result = new
{
    kind = "synthetic-ap-parse-aggregate-storage", sourceCommit, count,
    executedAssemblies = new[] { Assembly.GetExecutingAssembly(), typeof(SqliteCollectionStore).Assembly, typeof(ProfileSnapshot).Assembly }
        .Distinct().Select(assembly => new { file = Path.GetFileName(assembly.Location),
            sha256 = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(assembly.Location))) }).ToArray(),
    createdAtUtc = DateTimeOffset.UtcNow, operatingSystem = RuntimeInformation.OSDescription,
    processArchitecture = RuntimeInformation.ProcessArchitecture.ToString(), runtime = RuntimeInformation.FrameworkDescription,
    source = "generated Demo XML with reserved documentation addresses; no network requests", frames = records,
    initializeMilliseconds = initialization.Elapsed.TotalMilliseconds, reopenAndTwoQueriesMilliseconds = reopen.Elapsed.TotalMilliseconds,
    initialDatabaseBytes = beforeBytes, finalDatabaseBytes = DatabaseBytes(location),
    databaseGrowthBytes = DatabaseBytes(location) - beforeBytes, backupMilliseconds = backupTimer.Elapsed.TotalMilliseconds,
    backupBytes = backup.Bytes, backup.Sha256, retentionMilliseconds = retentionTimer.Elapsed.TotalMilliseconds, retention,
    totalMilliseconds = total.Elapsed.TotalMilliseconds, processMemory = Memory(), checksPassed = true,
    limitations = new[] { "one process per size; process peak includes previous frames and backup", "synthetic local disk only",
        "parsing and transactional aggregation/storage measured separately; no HTTP latency", "first page query is up to 500 rows",
        "database bytes include main/WAL/SHM when present; no VACUUM", "no throughput or production SLA implied" }
};
var json = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
await File.WriteAllTextAsync(Path.Combine(output, "measurement.json"), json + Environment.NewLine);
Console.WriteLine(json);

static void Require(bool success, string description)
{
    if (!success) throw new InvalidDataException("Measurement correctness check failed: " + description);
}
static long DatabaseBytes(StorageLocation location) => new[] { "monitor.db", "monitor.db-wal", "monitor.db-shm" }
    .Select(name => new FileInfo(Path.Combine(location.DataDirectory, name))).Where(file => file.Exists).Sum(file => file.Length);
static object Memory()
{
    using var process = Process.GetCurrentProcess();
    process.Refresh();
    return new { workingSetBytes = process.WorkingSet64, processLifetimePeakWorkingSetBytes = process.PeakWorkingSet64 > 0 ? process.PeakWorkingSet64 : (long?)null,
        managedHeapBytes = GC.GetTotalMemory(false), managedTotalAllocatedBytes = GC.GetTotalAllocatedBytes() };
}
static byte[] CreateXml(int count, bool? up)
{
    var xml = new StringBuilder("<amp:amp_ap_list xmlns:amp=\"http://www.airwave.com\">");
    for (var i = 1; i <= count; i++)
    {
        xml.Append(CultureInfo.InvariantCulture, $"<ap id=\"{i}\"><device_category>thin_ap</device_category><name>Synthetic AP {i:D5}</name><serial_number>SYN-{i:D5}</serial_number><lan_mac>02:00:00:{(i >> 16) & 255:x2}:{(i >> 8) & 255:x2}:{i & 255:x2}</lan_mac><lan_ip>192.0.2.{1 + i % 250}</lan_ip><model>Synthetic</model><firmware>fixture-1</firmware>");
        if (up is not null) xml.Append(up.Value ? "<is_up>true</is_up>" : "<is_up>false</is_up>");
        xml.Append("</ap>");
    }
    return Encoding.UTF8.GetBytes(xml.Append("</amp:amp_ap_list>").ToString());
}
sealed class MeasurementClock : TimeProvider
{
    public DateTimeOffset Now { get; set; } = DateTimeOffset.Parse("2026-11-16T00:00:00Z", CultureInfo.InvariantCulture);
    public override DateTimeOffset GetUtcNow() => Now;
}
