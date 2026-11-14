using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using MediaUnseal.Services;

var timing = new CliTiming(args.Contains("--diagnostics") || args.Contains("--diagnostics-repeat"));
Console.OutputEncoding = Encoding.UTF8;
using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
try { return await RunAsync(args, cancellation.Token, timing); }
catch (OperationCanceledException) { Console.Error.WriteLine("Cancelled."); return 5; }
catch (ArgumentException ex) { Console.Error.WriteLine(ex.Message); return 2; }
catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
{ Console.Error.WriteLine(ex.Message); return 3; }
finally { timing.Report(); }

static async Task<int> RunAsync(string[] args, CancellationToken token, CliTiming timing)
{
    if (args.Length == 0 || args.Contains("--help") || args.Contains("-h")) { Help(); return 0; }
    if (args[0] == "app")
    {
        if (args.Length == 1) { Help(); return 0; }
        var command = string.Join(' ', args.Skip(1));
        if (command is not ("version" or "tools check" or "tools install" or "tools path"
            or "update check" or "update download" or "config show" or "config set"
            or "logs export" or "languages list")) throw new ArgumentException("Unknown app command.");
        Console.Error.WriteLine("Not implemented: app " + command);
        return 6;
    }
    if (args[0] == "mkv" && args.Length >= 2 && args[1] == "chapters")
        return await ChaptersAsync(args, token, timing);
    if (args[0] != "mkv" || args.Length < 2 || args[1] is not ("scan" or "info" or "structure" or "extract"))
        throw new ArgumentException("Unknown command. Use --help.");
    var commandName = args[1];
    string? input = null, output = null;
    ulong? id = null;
    var all = false; var json = false; var quiet = false; var thorough = false;
    int? diagnosticRepeats = null;
    for (var i = 2; i < args.Length; i++)
        switch (args[i])
        {
            case "-i" or "--input" when i + 1 < args.Length: input = args[++i]; break;
            case "-o" or "--output" when i + 1 < args.Length: output = args[++i]; break;
            case "--id" when i + 1 < args.Length:
                if (!ulong.TryParse(args[++i], NumberStyles.None, CultureInfo.InvariantCulture, out var value) || value == 0)
                    throw new ArgumentException("--id requires a positive decimal FileUID.");
                id = value; break;
            case "--all": all = true; break;
            case "--json": json = true; break;
            case "--quiet": quiet = true; break;
            case "--thorough": thorough = true; break;
            case "--diagnostics": break;
            case "--diagnostics-repeat" when i + 1 < args.Length:
                if (!int.TryParse(args[++i], NumberStyles.None, CultureInfo.InvariantCulture, out var repeats)
                    || repeats is < 2 or > 20 || diagnosticRepeats is not null)
                    throw new ArgumentException("--diagnostics-repeat requires one integer from 2 to 20.");
                diagnosticRepeats = repeats; break;
            default: throw new ArgumentException("Unknown or incomplete option: " + args[i]);
        }
    if (string.IsNullOrWhiteSpace(input)) throw new ArgumentException("-i/--input is required.");
    if (commandName == "extract" && (string.IsNullOrWhiteSpace(output) || all == id.HasValue))
        throw new ArgumentException("extract requires -o and exactly one of --all or --id.");
    if (commandName != "extract" && (output is not null || all || id.HasValue))
        throw new ArgumentException("-o, --all and --id are available only for extract.");
    if (json && quiet) throw new ArgumentException("--json and --quiet cannot be combined.");
    if (thorough && commandName != "scan") throw new ArgumentException("--thorough is available only for scan.");
    if (diagnosticRepeats is not null && commandName != "scan")
        throw new ArgumentException("--diagnostics-repeat is available only for mkv scan.");
    var source = Path.GetFullPath(input);
    timing.BeginOperation();
    var searchWatch = Stopwatch.StartNew();
    var stats = new MatroskaAttachments.NavigationStats { CollectPerformanceDetails = timing.Enabled };
    timing.Navigation = stats;
    try
    {
    var openStart = timing.Stamp();
    await using var stream = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read,
        4096, FileOptions.Asynchronous | FileOptions.RandomAccess);
    var length = stream.Length;
    timing.OpenTime += timing.Since(openStart);
    var parseStart = timing.Stamp();
    var discovery = commandName == "extract" || thorough
        ? new MatroskaAttachments.DiscoveryResult(
            commandName == "extract"
                ? await MatroskaAttachments.ReadForExportAsync(stream, token, stats)
                : await MatroskaAttachments.ReadAsync(stream, token, stats),
            stats.AttachmentsOffset is null ? MatroskaAttachments.DiscoveryStatus.CompleteNoAttachments
                : MatroskaAttachments.DiscoveryStatus.Found)
        : await MatroskaAttachments.DiscoverStandardAsync(stream, token, stats);
    var attachments = discovery.Attachments;
    if (stream.Length != length) throw new InvalidDataException("The input changed while scanning.");
    timing.SearchTime += timing.Since(parseStart);
    timing.ResultStatus = discovery.Status;
    timing.OpenAndSearch = searchWatch.Elapsed;
    if (diagnosticRepeats is { } passCount)
    {
        await stream.DisposeAsync();
        timing.ReportPass(1, timing.OpenTime, timing.SearchTime, searchWatch.Elapsed, stats, discovery.Status);
        for (var pass = 2; pass <= passCount; pass++)
        {
            token.ThrowIfCancellationRequested();
            var passWatch = Stopwatch.StartNew();
            var repeatOpenStart = Stopwatch.GetTimestamp();
            var repeatStats = new MatroskaAttachments.NavigationStats { CollectPerformanceDetails = true };
            MatroskaAttachments.DiscoveryResult repeatDiscovery;
            TimeSpan repeatOpen, repeatSearch;
            await using (var repeatStream = new FileStream(source, FileMode.Open, FileAccess.Read,
                FileShare.Read, 4096, FileOptions.Asynchronous | FileOptions.RandomAccess))
            {
                var repeatLength = repeatStream.Length;
                repeatOpen = Stopwatch.GetElapsedTime(repeatOpenStart);
                var repeatSearchStart = Stopwatch.GetTimestamp();
                repeatDiscovery = thorough
                    ? new MatroskaAttachments.DiscoveryResult(
                        await MatroskaAttachments.ReadAsync(repeatStream, token, repeatStats),
                        repeatStats.AttachmentsOffset is null
                            ? MatroskaAttachments.DiscoveryStatus.CompleteNoAttachments
                            : MatroskaAttachments.DiscoveryStatus.Found)
                    : await MatroskaAttachments.DiscoverStandardAsync(repeatStream, token, repeatStats);
                if (repeatStream.Length != repeatLength)
                    throw new InvalidDataException("The input changed while scanning.");
                repeatSearch = Stopwatch.GetElapsedTime(repeatSearchStart);
            }
            timing.ReportPass(pass, repeatOpen, repeatSearch, passWatch.Elapsed,
                repeatStats, repeatDiscovery.Status);
        }
    }
    if (commandName == "extract")
    {
        var selected = id is { } uid ? attachments.Where(a => a.Uid == uid).ToArray() : attachments.ToArray();
        if (id.HasValue && selected.Length == 0) throw new InvalidDataException("FileUID not found.");
        var prepareStart = timing.Stamp();
        var directory = Path.GetFullPath(output!);
        var guard = new OutputPathGuard(directory);
        timing.PreparationTime += timing.Since(prepareStart);
        var copyWatch = Stopwatch.StartNew();
        try
        {
        foreach (var attachment in selected)
        {
            token.ThrowIfCancellationRequested();
            var validationStart = timing.Stamp();
            guard.Validate(directory);
            timing.SafetyCheckTime += timing.Since(validationStart);
            var nameStart = timing.Stamp();
            var safe = AttachmentFileNames.Safe(attachment.Name);
            var stem = Path.GetFileNameWithoutExtension(safe);
            var extension = Path.GetExtension(safe);
            string? destination = null;
            for (var attempt = 1; attempt < 10_000; attempt++)
            {
                var candidate = Path.Combine(directory, attempt == 1 ? safe : $"{stem} ({attempt}){extension}");
                if (File.Exists(candidate) || Directory.Exists(candidate)) continue;
                destination = candidate; break;
            }
            if (destination is null) throw new IOException("No free output filename.");
            timing.PreparationTime += timing.Since(nameStart);
            validationStart = timing.Stamp();
            guard.Validate(directory);
            timing.SafetyCheckTime += timing.Since(validationStart);
            var createStart = timing.Stamp();
            await using var target = new FileStream(destination, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
            timing.FileCreateTime += timing.Since(createStart);
            try
            {
                await MatroskaAttachments.CopyAsync(stream, target, attachment, null, token,
                    timing.Enabled ? stats : null);
                var flushStart = timing.Stamp();
                await target.FlushAsync(token);
                timing.FlushTime += timing.Since(flushStart);
                if (stream.Length != length) throw new InvalidDataException("The input changed during extraction.");
            }
            catch
            {
                await target.DisposeAsync();
                try { File.Delete(destination); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                throw;
            }
            if (!quiet && !json) Console.WriteLine($"Extracted FileUID {attachment.Uid}: {destination}");
        }
        }
        finally { timing.CopyAndWrite = copyWatch.Elapsed; }
        if (json) WriteJson(writer => writer.WriteNumber("extracted", selected.Length));
        else if (quiet) Console.WriteLine(selected.Length.ToString(CultureInfo.InvariantCulture));
        return 0;
    }
    if (json) WriteJson(writer =>
    {
        writer.WriteNumber("count", attachments.Count);
        writer.WriteString("status", discovery.Status.ToString());
        if (commandName == "structure")
        {
            if (stats.SegmentOffset is { } segment) writer.WriteNumber("segmentOffset", segment);
            writer.WriteStartArray("seekHeadOffsets");
            foreach (var offset in stats.SeekHeadOffsets) writer.WriteNumberValue(offset);
            writer.WriteEndArray();
            if (stats.AttachmentsOffset is { } attachmentOffset) writer.WriteNumber("attachmentsOffset", attachmentOffset);
            writer.WriteBoolean("usedSeekHead", stats.UsedSeekHead);
        }
        else
        {
            writer.WriteStartArray("attachments");
            foreach (var a in attachments)
            {
                writer.WriteStartObject(); writer.WriteNumber("fileUid", a.Uid);
                if (commandName == "info")
                { writer.WriteString("name", a.Name); writer.WriteString("mime", a.Mime);
                  if (a.Description is not null) writer.WriteString("description", a.Description);
                  writer.WriteNumber("size", a.Size); writer.WriteNumber("dataOffset", a.Offset); }
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }
    });
    else if (quiet)
    {
        Console.WriteLine(attachments.Count.ToString(CultureInfo.InvariantCulture));
        if (discovery.Status is MatroskaAttachments.DiscoveryStatus.Incomplete or
            MatroskaAttachments.DiscoveryStatus.NotFoundByIndex)
            Console.Error.WriteLine("Standard search is incomplete; use mkv scan --thorough for a full search.");
    }
    else if (commandName == "structure")
        Console.WriteLine($"Segment: {stats.SegmentOffset?.ToString() ?? "not found"}; SeekHead: {string.Join(", ", stats.SeekHeadOffsets)}; Attachments: {stats.AttachmentsOffset?.ToString() ?? "none"}; indexed: {stats.UsedSeekHead}");
    else
    {
        Console.WriteLine($"Attachments: {attachments.Count}; status: {discovery.Status}");
        if (discovery.Status is MatroskaAttachments.DiscoveryStatus.Incomplete or
            MatroskaAttachments.DiscoveryStatus.NotFoundByIndex)
            Console.WriteLine("Standard search is incomplete; use mkv scan --thorough for a full search.");
        foreach (var a in attachments)
            Console.WriteLine(commandName == "scan" ? $"FileUID {a.Uid}" : $"FileUID {a.Uid}: {a.Name}; {a.Mime}; {a.Size} bytes; description: {a.Description ?? "—"}; data offset: {a.Offset}");
    }
    return 0;
    }
    finally { timing.OpenAndSearch ??= searchWatch.Elapsed; }
}

static async Task<int> ChaptersAsync(string[] args, CancellationToken token, CliTiming timing)
{
    string? input = null, output = null;
    var json = false;
    for (var i = 2; i < args.Length; i++)
        switch (args[i])
        {
            case "-i" or "--input" when i + 1 < args.Length: input = args[++i]; break;
            case "-o" or "--output" when i + 1 < args.Length: output = args[++i]; break;
            case "--json": json = true; break;
            case "--diagnostics": break;
            default: throw new ArgumentException("Unknown or incomplete option: " + args[i]);
        }
    if (string.IsNullOrWhiteSpace(input) || json && output is not null)
        throw new ArgumentException("chapters requires -i and either --json or -o FILE.xml.");
    if (output is not null && !Path.GetExtension(output).Equals(".xml", StringComparison.OrdinalIgnoreCase))
        throw new ArgumentException("Chapter output must use the .xml extension.");
    timing.BeginOperation();
    var stats = new MatroskaAttachments.NavigationStats { CollectPerformanceDetails = timing.Enabled };
    timing.Navigation = stats;
    var opened = timing.Stamp();
    await using var stream = new FileStream(Path.GetFullPath(input), FileMode.Open, FileAccess.Read,
        FileShare.Read, 4096, FileOptions.Asynchronous | FileOptions.RandomAccess);
    timing.OpenTime = timing.Since(opened);
    var length = stream.Length;
    var started = timing.Stamp();
    var chapters = await MatroskaAttachments.ReadChaptersAsync(stream, token, stats);
    if (stream.Length != length) throw new InvalidDataException("The input changed while scanning.");
    timing.SearchTime = timing.Since(started);
    timing.OpenAndSearch = timing.OpenTime + timing.SearchTime;
    if (output is not null)
    {
        if (chapters.Count == 0) throw new InvalidDataException("No chapters found.");
        var destination = Path.GetFullPath(output);
        var directory = Path.GetDirectoryName(destination)!;
        if (!Directory.Exists(directory) || File.Exists(destination))
            throw new IOException("Output directory is missing or output file already exists.");
        var guard = new OutputPathGuard(directory);
        guard.Validate(directory);
        var temporary = Path.Combine(directory, ".mediaunseal-chapters-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await File.WriteAllTextAsync(temporary, chapters.ToXml(), new UTF8Encoding(false), token);
            guard.Validate(directory);
            File.Move(temporary, destination, overwrite: false);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        Console.WriteLine(destination);
        if (chapters.HasChapterStringUid)
            Console.Error.WriteLine("Warning: ChapterStringUID values were omitted from XML; all other supported chapter data was preserved.");
    }
    else if (json)
    {
        using var writer = new Utf8JsonWriter(Console.OpenStandardOutput(), new JsonWriterOptions { Indented = true });
        writer.WriteStartObject(); writer.WriteNumber("count", chapters.Count);
        writer.WritePropertyName("editions"); writer.WriteStartArray();
        foreach (var edition in chapters.Editions) WriteChapterJson(writer, edition);
        writer.WriteEndArray(); writer.WriteEndObject(); writer.Flush(); Console.WriteLine();
    }
    else
    {
        Console.WriteLine($"Editions: {chapters.Editions.Count}; chapters: {chapters.Count}");
        foreach (var edition in chapters.Editions) PrintChapter(edition, 0);
    }
    return 0;
}

static void WriteChapterJson(Utf8JsonWriter writer, MatroskaAttachments.ChapterNode node)
{
    writer.WriteStartObject(); writer.WriteString("name", node.Name);
    if (node.Value is not null) writer.WriteString("value", node.Value);
    writer.WritePropertyName("children"); writer.WriteStartArray();
    foreach (var child in node.Children) WriteChapterJson(writer, child);
    writer.WriteEndArray(); writer.WriteEndObject();
}

static void PrintChapter(MatroskaAttachments.ChapterNode node, int depth)
{
    Console.WriteLine(new string(' ', depth * 2) + node.Name + (node.Value is null ? "" : ": " + node.Value));
    foreach (var child in node.Children) PrintChapter(child, depth + 1);
}

static void WriteJson(Action<Utf8JsonWriter> write)
{
    using var writer = new Utf8JsonWriter(Console.OpenStandardOutput(), new JsonWriterOptions { Indented = true });
    writer.WriteStartObject(); write(writer); writer.WriteEndObject(); writer.Flush();
    Console.WriteLine();
}

static void Help() => Console.WriteLine("""
Media Unseal CLI
Usage: mucli mkv <scan|info|structure|extract|chapters> -i FILE [options]
  -i, --input FILE     Matroska input file
  -o, --output DIR     Existing output directory (extract)
  --id UID             Select decimal attachment FileUID (extract)
  --all                Select every attachment (extract)
  --json               JSON output
  --quiet              Minimal output
  --thorough           Full structural scan (mkv scan only)
  --diagnostics        Write process timings and parser statistics to stderr
  --diagnostics-repeat N  Diagnostic scan in one process, 2-20 passes
  mkv chapters -i FILE             Show chapter editions and hierarchy
  mkv chapters -i FILE --json      Structured chapter data
  mkv chapters -i FILE -o FILE.xml Export MKVToolNix chapter XML
  --help               Show help

Future commands (Not implemented):
  app version
  app tools check|install|path
  app update check|download
  app config show|set
  app logs export
  app languages list
""");

sealed class CliTiming(bool enabled)
{
    private readonly Stopwatch _total = Stopwatch.StartNew();
    internal bool Enabled => enabled;
    private TimeSpan? _beforeOperation;
    internal TimeSpan? OpenAndSearch { get; set; }
    internal TimeSpan OpenTime { get; set; }
    internal TimeSpan SearchTime { get; set; }
    internal TimeSpan? CopyAndWrite { get; set; }
    internal TimeSpan PreparationTime { get; set; }
    internal TimeSpan SafetyCheckTime { get; set; }
    internal TimeSpan FileCreateTime { get; set; }
    internal TimeSpan FlushTime { get; set; }
    internal MatroskaAttachments.DiscoveryStatus? ResultStatus { get; set; }
    internal MatroskaAttachments.NavigationStats? Navigation { get; set; }

    internal void BeginOperation() => _beforeOperation = _total.Elapsed;
    internal long Stamp() => enabled ? Stopwatch.GetTimestamp() : 0;
    internal TimeSpan Since(long start) => start == 0 ? TimeSpan.Zero : Stopwatch.GetElapsedTime(start);

    internal void ReportPass(int pass, TimeSpan open, TimeSpan search, TimeSpan total,
        MatroskaAttachments.NavigationStats stats, MatroskaAttachments.DiscoveryStatus status)
    {
        static string Ms(TimeSpan time) => time.TotalMilliseconds.ToString("F3", CultureInfo.InvariantCulture);
        Console.Error.WriteLine($"[diagnostics] Pass {pass}: open={Ms(open)} ms; " +
            $"EBML search={Ms(search)} ms; total={Ms(total)} ms; status={status}");
        Console.Error.WriteLine($"[diagnostics] Pass {pass} NavigationStats: " +
            $"usedSeekHead={stats.UsedSeekHead}; indexJumps={stats.IndexJumps}; " +
            $"elements={stats.Elements}; positionChanges={stats.PositionChanges}; " +
            $"searchBytesRead={stats.SearchBytesRead}; readCalls={stats.ReadCalls}; " +
            $"readTime={Ms(stats.ReadTime)} ms; headerCalls={stats.HeaderCalls}; " +
            $"headerTime={Ms(stats.HeaderTime)} ms; " +
            $"positionAssignmentTime={Ms(stats.PositionChangeTime)} ms");
    }

    internal void Report()
    {
        if (!enabled) return;
        _total.Stop();
        static string Ms(TimeSpan? value) => value is { } time
            ? time.TotalMilliseconds.ToString("F3", CultureInfo.InvariantCulture) + " ms" : "n/a";
        Console.Error.WriteLine($"[diagnostics] Main to operation: {Ms(_beforeOperation)}");
        Console.Error.WriteLine($"[diagnostics] Open and attachment search: {Ms(OpenAndSearch)}");
        Console.Error.WriteLine($"[diagnostics] MKV open: {Ms(OpenTime)}; EBML search: {Ms(SearchTime)}");
        Console.Error.WriteLine($"[diagnostics] Copy and write: {Ms(CopyAndWrite)}");
        if (CopyAndWrite is not null)
            Console.Error.WriteLine($"[diagnostics] Extract stages: preparation={Ms(PreparationTime)}; " +
                $"safetyChecks={Ms(SafetyCheckTime)}; fileCreate={Ms(FileCreateTime)}; " +
                $"flush={Ms(FlushTime)}; file disposal and remaining work included in Copy and write");
        Console.Error.WriteLine($"[diagnostics] Total in process: {Ms(_total.Elapsed)}");
        if (Navigation is not { } stats) return;
        Console.Error.WriteLine($"[diagnostics] NavigationStats: status={ResultStatus?.ToString() ?? "n/a"}; " +
            $"segmentOffset={stats.SegmentOffset?.ToString(CultureInfo.InvariantCulture) ?? "n/a"}; " +
            $"seekHeadOffsets={string.Join(',', stats.SeekHeadOffsets)}; " +
            $"attachmentsOffset={stats.AttachmentsOffset?.ToString(CultureInfo.InvariantCulture) ?? "n/a"}; " +
            $"usedSeekHead={stats.UsedSeekHead}; indexJumps={stats.IndexJumps}; " +
            $"positionChanges={stats.PositionChanges}; elements={stats.Elements}; " +
            $"searchBytesRead={stats.SearchBytesRead}; copyBytesRead={stats.CopyBytesRead}; " +
            $"searchElapsed={Ms(stats.SearchElapsed == default ? null : stats.SearchElapsed)}; " +
            $"matchElapsed={Ms(stats.MatchElapsed == default ? null : stats.MatchElapsed)}; " +
            $"copyElapsed={Ms(stats.CopyElapsed == default ? null : stats.CopyElapsed)}");
        Console.Error.WriteLine($"[diagnostics] EBML details: headerCalls={stats.HeaderCalls}; " +
            $"headerTime={Ms(stats.HeaderTime)}; readCalls={stats.ReadCalls}; readTime={Ms(stats.ReadTime)}; " +
            $"positionChanges={stats.PositionChanges}; positionAssignmentTime={Ms(stats.PositionChangeTime)}");
        if (CopyAndWrite is not null)
            Console.Error.WriteLine($"[diagnostics] Copy I/O: readCalls={stats.CopyReadCalls}; " +
                $"readTime={Ms(stats.CopyReadTime)}; writeCalls={stats.CopyWriteCalls}; " +
                $"writeTime={Ms(stats.CopyWriteTime)}");
    }
}
