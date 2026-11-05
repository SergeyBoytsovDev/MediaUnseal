using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MediaUnseal.Models;

namespace MediaUnseal.Services;

// A bounded reader of Segment/Attachments only. Media payloads are skipped by their
// EBML sizes, never searched for byte signatures. FFprobe indexes are not FileUIDs.
internal static class MatroskaAttachments
{
    internal sealed record Attachment(ulong Uid, string Name, string Mime, long Offset, long Size,
        string? Description = null);

    internal static bool IsMatroska(MediaInfo media) =>
        media.Container?.Split(',', StringSplitOptions.TrimEntries).Contains("matroska") == true;

    internal static bool ProbeReportsAttachments(MediaInfo media) => media.Streams.Any(stream =>
        stream.CodecType == "attachment" || stream.Disposition.GetValueOrDefault("attached_pic") == 1
        || stream.Tags.ContainsKey("filename") && stream.Tags.ContainsKey("mimetype"));

    internal static async Task<IReadOnlyList<Attachment>> ReadFileAsync(MediaInfo media,
        CancellationToken cancellationToken, NavigationStats? stats = null)
    {
        if (!IsMatroska(media)) return [];
        await using var input = new FileStream(media.FullPath, FileMode.Open, FileAccess.Read,
            FileShare.Read, 4096, FileOptions.Asynchronous | FileOptions.RandomAccess);
        if (input.Length != media.Size)
            throw new InvalidDataException("The source file changed after analysis.");
        return await ReadForExportAsync(input, cancellationToken, stats ?? new NavigationStats()).ConfigureAwait(false);
    }

    // FFprobe exposes AttachedFile streams in file order. Validate the entire sequence
    // before assigning a FileUID; a filename alone is not an attachment identity.
    internal static IReadOnlyDictionary<int, ulong> MatchStreamUids(MediaInfo media,
        IReadOnlyList<Attachment> attachments)
    {
        var streams = media.Streams.Where(s => s.CodecType == "attachment"
            || s.CodecType == "video" && s.Disposition.GetValueOrDefault("attached_pic") == 1)
            .OrderBy(s => s.Index).ToArray();
        if (streams.Length != attachments.Count || streams.Any(s => s.Index is null or < 0)
            || streams.Select(s => s.Index).Distinct().Count() != streams.Length)
            return new Dictionary<int, ulong>();
        var matches = new Dictionary<int, ulong>();
        for (var i = 0; i < streams.Length; i++)
        {
            if (streams[i].Tags.GetValueOrDefault("filename") != attachments[i].Name
                || !string.Equals(streams[i].Tags.GetValueOrDefault("mimetype"), attachments[i].Mime,
                    StringComparison.OrdinalIgnoreCase))
                return new Dictionary<int, ulong>();
            matches.Add(streams[i].Index!.Value, attachments[i].Uid);
        }
        return matches;
    }
    internal sealed class NavigationStats
    {
        public bool UsedSeekHead { get; internal set; }
        public int IndexJumps { get; internal set; }
        public int PositionChanges { get; internal set; }
        public int Elements { get; internal set; }
        public long SearchBytesRead { get; internal set; }
        public long CopyBytesRead { get; internal set; }
        public TimeSpan SearchElapsed { get; internal set; }
        public TimeSpan MatchElapsed { get; internal set; }
        public TimeSpan CopyElapsed { get; internal set; }
    }

    internal static bool IsCandidate(MediaInfo media, MediaStreamInfo stream) =>
        media.Container?.Split(',', StringSplitOptions.TrimEntries).Contains("matroska") == true
        && stream.VisualKind == VisualKind.Image
        && (stream.CodecType == "attachment" || stream.Disposition.GetValueOrDefault("attached_pic") == 1);

    // Null means a valid Matroska file has no Attachments and this is an ordinary
    // picture stream. Corruption and failed/ambiguous matching always throw.
    internal static async Task<Attachment?> TryExtractAsync(MediaInfo media, MediaStreamInfo stream,
        string destination, IProgress<double>? progress, CancellationToken cancellationToken,
        Action<NavigationStats>? report = null)
    {
        var stats = new NavigationStats();
        var watch = Stopwatch.StartNew();
        var copying = false;
        var matching = false;
        try
        {
        await using var input = new FileStream(media.FullPath, FileMode.Open, FileAccess.Read,
            FileShare.Read, 4096, FileOptions.Asynchronous | FileOptions.RandomAccess);
        if (input.Length != media.Size)
            throw new InvalidDataException("The source file changed after analysis. Analyze it again.");
        var attachments = await ReadForExportAsync(input, cancellationToken, stats).ConfigureAwait(false);
        stats.SearchElapsed = watch.Elapsed;
        if (attachments.Count == 0 && stream.CodecType != "attachment"
            && !stream.Tags.ContainsKey("filename") && !stream.Tags.ContainsKey("mimetype")) return null;
        watch.Restart();
        matching = true;
        var attachment = Match(attachments, media, stream);
        stats.MatchElapsed = watch.Elapsed;
        matching = false;
        cancellationToken.ThrowIfCancellationRequested();
        // CreateNew protects even the temporary destination. Cleanup belongs to the
        // exporter's existing staging transaction, including write/cancel failures.
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write,
            FileShare.None, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
        watch.Restart();
        copying = true;
        await CopyAsync(input, output, attachment, progress, cancellationToken, stats).ConfigureAwait(false);
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        stats.CopyElapsed = watch.Elapsed;
        if (input.Length != media.Size)
            throw new InvalidDataException("The source file changed during attachment extraction.");
        return attachment;
        }
        finally
        {
            if (copying) stats.CopyElapsed = watch.Elapsed;
            else if (matching) stats.MatchElapsed = watch.Elapsed;
            else if (stats.SearchElapsed == default) stats.SearchElapsed = watch.Elapsed;
            report?.Invoke(stats);
        }
    }

    internal static Attachment Match(IReadOnlyList<Attachment> attachments, MediaInfo media, MediaStreamInfo stream)
    {
        if (!stream.Tags.TryGetValue("filename", out var name)
            || !stream.Tags.TryGetValue("mimetype", out var mime))
            throw new InvalidDataException("Not enough metadata to match the Matroska attachment. Analyze the file again.");
        var matches = attachments.Where(a => a.Name == name
            && string.Equals(a.Mime, mime, StringComparison.OrdinalIgnoreCase)).ToArray();
        var probeMatches = media.Streams.Count(s => IsCandidate(media, s)
            && s.Tags.GetValueOrDefault("filename") == name
            && string.Equals(s.Tags.GetValueOrDefault("mimetype"), mime, StringComparison.OrdinalIgnoreCase));
        if (stream.AttachmentUid is { } uid)
            matches = matches.Where(a => a.Uid == uid).ToArray();
        if (matches.Length != 1 || stream.AttachmentUid is null && probeMatches != 1)
            throw new InvalidDataException(matches.Length == 0
                ? "The selected attachment was not found in Matroska. Analyze the file again; FFmpeg extraction was not started."
                : "Ambiguous attachment match: duplicate name and MIME type. Extraction was stopped.");
        var expectedMime = stream.ImageFormat switch { "JPEG" => "image/jpeg", "PNG" => "image/png", _ => null };
        if (!string.Equals(mime, expectedMime, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The attachment MIME type does not match the detected image format.");
        // matroskadec creates streams for AttachedFile entries in document order,
        // then applies Tags (which can override filename/mimetype). Check the whole
        // attachment sequence as well as uniqueness, so swapped/overridden tags do
        // not silently redirect a selected stream to another FileData. Do not infer
        // FileUID or MKVToolNix attachment IDs from FFprobe indexes.
        var probeAttachments = media.Streams.Where(s => s.CodecType == "attachment"
            || (s.CodecType == "video" && s.Disposition.GetValueOrDefault("attached_pic") == 1))
            .OrderBy(s => s.Index).ToArray();
        if (probeAttachments.Length != attachments.Count
            || probeAttachments.Any(s => s.Index is null or < 0)
            || probeAttachments.Select(s => s.Index).Distinct().Count() != probeAttachments.Length)
            throw new InvalidDataException("The Matroska attachment list differs from FFprobe. Analyze the file again.");
        for (var i = 0; i < attachments.Count; i++)
            if (probeAttachments[i].Tags.GetValueOrDefault("filename") != attachments[i].Name
                || !string.Equals(probeAttachments[i].Tags.GetValueOrDefault("mimetype"),
                    attachments[i].Mime, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Matroska attachment order or metadata differs from FFprobe. Extraction was stopped.");
        return matches[0];
    }

    internal static async Task CopyAsync(Stream input, Stream output, Attachment attachment,
        IProgress<double>? progress, CancellationToken cancellationToken, NavigationStats? stats = null)
    {
        if (attachment.Offset < 0 || attachment.Size <= 0 || attachment.Offset > input.Length
            || attachment.Size > input.Length - attachment.Offset)
            throw new InvalidDataException("Attachment data extends beyond the file bounds.");
        if (stats is not null && input.Position != attachment.Offset) stats.PositionChanges++;
        input.Position = attachment.Offset;
        var buffer = new byte[65536];
        long copied = 0;
        var lastReported = -1.0;
        while (copied < attachment.Size)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = await input.ReadAsync(buffer.AsMemory(0,
                (int)Math.Min(buffer.Length, attachment.Size - copied)), cancellationToken).ConfigureAwait(false);
            if (count == 0) throw new InvalidDataException("Attachment data ended unexpectedly.");
            if (stats is not null) stats.CopyBytesRead += count;
            await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
            copied += count;
            var fraction = (double)copied / attachment.Size;
            if (fraction - lastReported >= 0.01 || copied == attachment.Size)
            {
                progress?.Report(fraction);
                lastReported = fraction;
            }
        }
    }

    internal static async Task CopySelectedAsync(MediaInfo media, AttachmentFile expected, string destination,
        IProgress<double>? progress, CancellationToken cancellationToken)
    {
        await using var input = new FileStream(media.FullPath, FileMode.Open, FileAccess.Read,
            FileShare.Read, 4096, FileOptions.Asynchronous | FileOptions.RandomAccess);
        if (input.Length != media.Size) throw new InvalidDataException("The source file changed after analysis.");
        var entries = await ReadForExportAsync(input, cancellationToken, new NavigationStats()).ConfigureAwait(false);
        var actual = entries.SingleOrDefault(a => a.Uid == expected.Uid);
        if (actual is null || actual.Name != expected.Name || actual.Mime != expected.Mime
            || actual.Size != expected.Size)
            throw new InvalidDataException("The selected attachment changed after analysis.");
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write,
            FileShare.None, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await CopyAsync(input, output, actual, progress, cancellationToken).ConfigureAwait(false);
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        if (input.Length != media.Size) throw new InvalidDataException("The source file changed during extraction.");
    }

    internal static async Task<IReadOnlyList<Attachment>> ReadAsync(Stream input, CancellationToken cancellationToken,
        NavigationStats? stats = null)
    {
        if (!input.CanRead || !input.CanSeek) throw new InvalidDataException("Matroska requires a seekable local file.");
        try { return await new Reader(input, cancellationToken, stats ?? new NavigationStats(), false, false).ReadAsync().ConfigureAwait(false); }
        catch (EndOfStreamException ex) { throw new InvalidDataException("Truncated EBML/Matroska structure.", ex); }
        catch (DecoderFallbackException ex) { throw new InvalidDataException("Corrupt Matroska metadata string.", ex); }
    }

    // The export path checks the indexed attachment against FFprobe's stream list.
    // Once that target is validated, scanning later Cluster headers provides no
    // data needed to copy the selected attachment.
    private static async Task<IReadOnlyList<Attachment>> ReadForExportAsync(Stream input,
        CancellationToken cancellationToken, NavigationStats stats)
    {
        try { return await new Reader(input, cancellationToken, stats, false, true).ReadAsync().ConfigureAwait(false); }
        catch (EndOfStreamException ex) { throw new InvalidDataException("Truncated EBML/Matroska structure.", ex); }
        catch (DecoderFallbackException ex) { throw new InvalidDataException("Corrupt Matroska metadata string.", ex); }
    }

    // Preview only needs a confirmed image attachment. It never creates an exported file;
    // Match still verifies FFprobe metadata and attachment ordering before bytes are shown.
    internal static async Task<IReadOnlyList<Attachment>> ReadPreviewAsync(Stream input,
        CancellationToken cancellationToken, NavigationStats? stats = null)
    {
        if (!input.CanRead || !input.CanSeek)
            throw new InvalidDataException("Matroska requires a seekable local file.");
        try { return await new Reader(input, cancellationToken, stats ?? new NavigationStats(), true, false).ReadAsync().ConfigureAwait(false); }
        catch (EndOfStreamException ex) { throw new InvalidDataException("Truncated EBML/Matroska structure.", ex); }
        catch (DecoderFallbackException ex) { throw new InvalidDataException("Corrupt Matroska metadata string.", ex); }
    }

    private sealed class Reader(Stream input, CancellationToken cancellationToken, NavigationStats stats,
        bool previewOnly, bool exportIndexedFast)
    {
        private const uint Segment = 0x18538067, Attachments = 0x1941A469, Cluster = 0x1F43B675;
        private const uint SeekHead = 0x114D9B74, Seek = 0x4DBB, SeekId = 0x53AB, SeekPosition = 0x53AC;
        private const int MaxSeekEntries = 4096, MaxSeekHeads = 32;
        private readonly byte[] _scratch = new byte[8];
        private readonly List<Attachment> _attachments = new();
        private readonly HashSet<ulong> _uids = new();
        private int _elements;
        private int _textBytes;
        private bool _foundAttachments;
        private int _maxSizeWidth = 8;
        private bool _headerRead;
        private readonly long _length = input.Length;
        private readonly HashSet<long> _visitedSeekHeads = new();
        private int _seekEntries;

        private readonly record struct Element(uint Id, long Start, long Data, long? End);

        internal async Task<IReadOnlyList<Attachment>> ReadAsync()
        {
            MoveTo(0);
            var header = await HeaderAsync(_length).ConfigureAwait(false);
            if (header.Id != 0x1A45DFA3 || header.End is null || header.End - header.Data > 65536)
                throw Bad("No valid EBML header");
            string? docType = null;
            var fields = new HashSet<uint>();
            while (input.Position < header.End)
            {
                var field = await HeaderAsync(header.End.Value).ConfigureAwait(false);
                RequireSize(field);
                if (!IsGlobal(field.Id) && !fields.Add(field.Id)) throw Bad("Duplicate EBML field");
                switch (field.Id)
                {
                    case 0x4282: docType = await TextAsync(field, false).ConfigureAwait(false); break;
                    case 0x42F7: if (await UIntAsync(field).ConfigureAwait(false) != 1) throw Bad("Unsupported EBML version"); break;
                    case 0x42F2: if (await UIntAsync(field).ConfigureAwait(false) != 4) throw Bad("Matroska requires EBMLMaxIDLength = 4"); break;
                    case 0x42F3:
                        var maxSizeWidth = await UIntAsync(field).ConfigureAwait(false);
                        if (maxSizeWidth is not (> 0 and <= 8)) throw Bad("Invalid EBML size length");
                        _maxSizeWidth = (int)maxSizeWidth;
                        break;
                    case 0x4285: if (await UIntAsync(field).ConfigureAwait(false) is not (> 0 and <= 4)) throw Bad("Unsupported Matroska version"); break;
                }
                MoveTo(field.End!.Value);
            }
            if (docType != "matroska") throw Bad("Not a Matroska document");
            _headerRead = true;
            var foundSegment = false;
            while (input.Position < _length)
            {
                var element = await HeaderAsync(_length).ConfigureAwait(false);
                if (element.Id == Segment)
                {
                    if (foundSegment) throw Bad("Multiple Segment elements are unsupported");
                    foundSegment = true;
                    if (await ReadSegmentAsync(element.Data, element.End ?? _length,
                        element.End is null || element.End == _length).ConfigureAwait(false))
                        break;
                }
                else if (IsGlobal(element.Id)) { RequireSize(element); MoveTo(element.End!.Value); }
                else throw Bad("Invalid element outside Segment");
            }
            if (!foundSegment) throw Bad("Segment not found");
            if (input.Length != _length) throw Bad("File size changed while reading");
            return _attachments;
        }

        private async Task<bool> ReadSegmentAsync(long segmentData, long end, bool allowIndex)
        {
            while (input.Position < end)
            {
                var element = await HeaderAsync(end).ConfigureAwait(false);
                if (!IsGlobal(element.Id) && element.Id < 0x10000000)
                    throw Bad("Segment-level element must have a four-byte ID");
                if (element.Id == Attachments)
                {
                    RequireSize(element);
                    if (_foundAttachments) throw Bad("Duplicate Attachments element");
                    _foundAttachments = true;
                    await ReadAttachmentsAsync(element.End!.Value).ConfigureAwait(false);
                    if (previewOnly) return true;
                }
                else if (element.Id == SeekHead)
                {
                    RequireSize(element);
                    if (allowIndex && !_foundAttachments
                        && await TryIndexedAttachmentsSafelyAsync(element, segmentData, end).ConfigureAwait(false))
                        return true;
                    MoveTo(element.End!.Value);
                }
                else if (element.Id == Cluster && element.End is null)
                    await SkipUnknownClusterAsync(end).ConfigureAwait(false);
                else
                {
                    RequireSize(element);
                    if (element.Id is Segment or 0x61A7 or 0x1A45DFA3) throw Bad("Invalid Matroska nesting");
                    MoveTo(element.End!.Value);
                }
            }
            return false;
        }

        private async Task<bool> TryIndexedAttachmentsSafelyAsync(Element head, long segmentData, long end)
        {
            try { return await TryIndexedAttachmentsAsync(head, segmentData, end).ConfigureAwait(false); }
            catch (InvalidDataException) { return false; }
            catch (EndOfStreamException) { return false; }
        }

        // An index is an optimization hint only. Every referenced header, size and
        // target type is checked before the existing AttachedFile reader sees it.
        private async Task<bool> TryIndexedAttachmentsAsync(Element first, long segmentData, long end)
        {
            var pending = new Queue<Element>();
            pending.Enqueue(first);
            long? attachmentOffset = null;
            var valid = true;
            while (pending.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_visitedSeekHeads.Count >= MaxSeekHeads) return false;
                var head = pending.Dequeue();
                if (!_visitedSeekHeads.Add(head.Start)) return false;
                if (head.End is null || head.End > end) return false;
                MoveTo(head.Data);
                while (input.Position < head.End)
                {
                    var child = await HeaderAsync(head.End.Value).ConfigureAwait(false);
                    RequireSize(child);
                    if (child.Id == Seek)
                    {
                        if (++_seekEntries > MaxSeekEntries) return false;
                        var entry = await ReadSeekEntryAsync(child.End!.Value).ConfigureAwait(false);
                        if (entry is null) valid = false;
                        if (entry is { } reference && reference.Id is Attachments or SeekHead)
                        {
                            if (reference.Position > (ulong)(end - segmentData - 1)) valid = false;
                            else
                            {
                                var targetOffset = segmentData + (long)reference.Position;
                                // The reference may never point into the index element itself.
                                if (targetOffset >= head.Start && targetOffset < head.End) valid = false;
                                else
                                {
                                    var resume = input.Position;
                                    MoveTo(targetOffset);
                                    stats.IndexJumps++;
                                    Element target;
                                    try { target = await HeaderAsync(end).ConfigureAwait(false); }
                                    catch (InvalidDataException) { valid = false; MoveTo(resume); continue; }
                                    if (target.Id != reference.Id || target.End is null || target.End <= target.Data)
                                        valid = false;
                                    else if (reference.Id == Attachments)
                                    {
                                        if (attachmentOffset is { } previous && previous != targetOffset) valid = false;
                                        attachmentOffset = targetOffset;
                                    }
                                    else if (!_visitedSeekHeads.Contains(targetOffset)) pending.Enqueue(target);
                                    else valid = false;
                                    MoveTo(resume);
                                }
                            }
                        }
                    }
                    MoveTo(child.End!.Value);
                }
            }
            if (!valid || attachmentOffset is null) return false;
            if (!await CheckIndexedPrefixAsync(first.End!.Value, attachmentOffset.Value, end).ConfigureAwait(false))
                return false;
            MoveTo(attachmentOffset.Value);
            var attachments = await HeaderAsync(end).ConfigureAwait(false);
            if (attachments.Id != Attachments || attachments.End is null) return false;
            stats.UsedSeekHead = true;
            _foundAttachments = true;
            await ReadAttachmentsAsync(attachments.End.Value).ConfigureAwait(false);
            if (previewOnly || exportIndexedFast) return true;
            // Keep checking the tail for duplicate Attachments and malformed
            // top-level elements. For the usual late attachment this is short.
            MoveTo(attachments.End.Value);
            await ReadSegmentAsync(segmentData, end, false).ConfigureAwait(false);
            return true;
        }

        private async Task<bool> CheckIndexedPrefixAsync(long from, long target, long end)
        {
            if (target < from) return false;
            // Verify the top-level envelope up to the target. An Attachments-like
            // byte sequence inside any Cluster is not a valid SeekHead target.
            while (from < target)
            {
                MoveTo(from);
                var element = await HeaderAsync(end).ConfigureAwait(false);
                if (element.Id == Attachments || element.End is null) return false;
                if (target < element.End.Value) return false;
                from = element.End.Value;
            }
            return true;
        }

        private async Task<(uint Id, ulong Position)?> ReadSeekEntryAsync(long end)
        {
            uint? id = null;
            ulong? position = null;
            while (input.Position < end)
            {
                var child = await HeaderAsync(end).ConfigureAwait(false);
                RequireSize(child);
                if (child.Id == SeekId)
                {
                    if (id is not null || child.End - child.Data is < 1 or > 4) return null;
                    var size = (int)(child.End!.Value - child.Data);
                    await ReadExactAsync(_scratch.AsMemory(0, size)).ConfigureAwait(false);
                    uint value = 0;
                    for (var i = 0; i < size; i++) value = (value << 8) | _scratch[i];
                    id = value;
                }
                else if (child.Id == SeekPosition)
                {
                    if (position is not null) return null;
                    position = await UIntAsync(child).ConfigureAwait(false);
                }
                else if (!IsGlobal(child.Id)) return null;
                MoveTo(child.End!.Value);
            }
            return id is { } foundId && position is { } foundPosition
                ? (foundId, foundPosition) : null;
        }

        // Unknown-size Clusters are allowed by Matroska. Walk only their immediate
        // child headers and skip Block payloads. Stop at a schema-defined level-1 ID.
        // No byte-signature search or recursion while walking Cluster children.
        private async Task SkipUnknownClusterAsync(long end)
        {
            while (input.Position < end)
            {
                var child = await HeaderAsync(end).ConfigureAwait(false);
                if (IsLevelOne(child.Id)) { MoveTo(child.Start); return; }
                RequireSize(child);
                if (child.Id is not (0xE7 or 0x5854 or 0xA7 or 0xAB or 0xA3 or 0xA0 or 0xAF or 0xEC or 0xBF))
                    throw Bad("Unknown structure in a Cluster of indeterminate size");
                MoveTo(child.End!.Value);
            }
        }

        private async Task ReadAttachmentsAsync(long end)
        {
            while (input.Position < end)
            {
                var file = await HeaderAsync(end).ConfigureAwait(false);
                RequireSize(file);
                if (file.Id == 0x61A7)
                {
                    if (_attachments.Count >= 1024) throw Bad("Attachment limit exceeded (1024)");
                    _attachments.Add(await ReadFileAsync(file.End!.Value).ConfigureAwait(false));
                }
                else if (!IsGlobal(file.Id)) throw Bad("Unexpected element in Attachments");
                MoveTo(file.End!.Value);
            }
            if (_attachments.Count == 0) throw Bad("Empty Attachments element");
        }

        private async Task<Attachment> ReadFileAsync(long end)
        {
            string? name = null, mime = null, description = null;
            ulong? uid = null;
            long? data = null, size = null;
            var fields = new HashSet<uint>();
            while (input.Position < end)
            {
                var field = await HeaderAsync(end).ConfigureAwait(false);
                RequireSize(field);
                if (IsLevelOne(field.Id) || field.Id is 0x61A7 or 0x1A45DFA3)
                    throw Bad("Invalid AttachedFile nesting");
                if (!IsGlobal(field.Id) && !fields.Add(field.Id)) throw Bad("Duplicate AttachedFile field");
                switch (field.Id)
                {
                    case 0x466E: name = await TextAsync(field, true).ConfigureAwait(false); break;
                    case 0x467E: description = await TextAsync(field, true).ConfigureAwait(false); break;
                    case 0x4660: mime = await TextAsync(field, false).ConfigureAwait(false); break;
                    case 0x46AE: uid = await UIntAsync(field).ConfigureAwait(false); break;
                    case 0x465C: data = field.Data; size = field.End - field.Data; break;
                    case Segment: throw Bad("Invalid AttachedFile nesting");
                }
                MoveTo(field.End!.Value);
            }
            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(mime) || uid is null or 0 || data is null || size is null or <= 0)
                throw Bad("Incomplete attachment metadata");
            if (!_uids.Add(uid.Value)) throw Bad("Duplicate attachment FileUID");
            return new(uid.Value, name, mime, data.Value, size.Value, description);
        }

        private async Task<Element> HeaderAsync(long parentEnd)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (++_elements > (previewOnly ? 512 : 100_000))
                throw Bad(previewOnly ? "Cover search limit exceeded (512 elements)"
                    : "EBML header limit exceeded (100000)");
            stats.Elements = _elements;
            var start = input.Position;
            var (id, _) = await VintAsync(parentEnd, true).ConfigureAwait(false);
            var (size, unknown) = await VintAsync(parentEnd, false).ConfigureAwait(false);
            var data = input.Position;
            if (unknown) return new((uint)id, start, data, null);
            if (size > (ulong)(parentEnd - data)) throw Bad("EBML size exceeds the parent or file bounds");
            return new((uint)id, start, data, data + (long)size);
        }

        private async Task<(ulong Value, bool Unknown)> VintAsync(long end, bool id)
        {
            if (input.Position >= end) throw Bad("Truncated EBML header");
            await ReadExactAsync(_scratch.AsMemory(0, 1)).ConfigureAwait(false);
            var first = _scratch[0];
            var width = 1;
            var marker = 0x80;
            while (marker != 0 && (first & marker) == 0) { marker >>= 1; width++; }
            if (marker == 0 || width > (id ? 4 : _headerRead ? _maxSizeWidth : 8)
                || width - 1 > end - input.Position)
                throw Bad("Invalid EBML VINT");
            ulong value = id ? first : (ulong)(first & (marker - 1));
            await ReadExactAsync(_scratch.AsMemory(0, width - 1)).ConfigureAwait(false);
            for (var i = 0; i < width - 1; i++) value = (value << 8) | _scratch[i];
            var mask = (1UL << (width * 7)) - 1;
            if (id && (value & mask) == mask) throw Bad("Reserved EBML ID");
            return (value, !id && value == mask);
        }

        private async Task<ulong> UIntAsync(Element field)
        {
            var size = field.End!.Value - field.Data;
            if (size is < 1 or > 8) throw Bad("Invalid EBML integer");
            await ReadExactAsync(_scratch.AsMemory(0, (int)size)).ConfigureAwait(false);
            ulong value = 0;
            for (var i = 0; i < size; i++) value = (value << 8) | _scratch[i];
            return value;
        }

        private async Task<string> TextAsync(Element field, bool utf8)
        {
            var size = field.End!.Value - field.Data;
            if (size > 4096 || (_textBytes += (int)size) > 1024 * 1024)
                throw Bad("Attachment metadata limit exceeded");
            var bytes = new byte[(int)size];
            await ReadExactAsync(bytes).ConfigureAwait(false);
            if (!utf8 && bytes.Any(b => b > 127)) throw Bad("Invalid Matroska ASCII string");
            var value = new UTF8Encoding(false, true).GetString(bytes).TrimEnd('\0');
            if (value.Contains('\0')) throw Bad("Null character in metadata");
            return value;
        }

        private static bool IsGlobal(uint id) => id is 0xEC or 0xBF;
        private void MoveTo(long position)
        {
            if (input.Position != position) stats.PositionChanges++;
            input.Position = position;
        }
        private async Task ReadExactAsync(Memory<byte> memory)
        {
            while (!memory.IsEmpty)
            {
                var count = await input.ReadAsync(memory, cancellationToken).ConfigureAwait(false);
                if (count == 0) throw new EndOfStreamException();
                stats.SearchBytesRead += count;
                memory = memory[count..];
            }
        }
        private static bool IsLevelOne(uint id) => id is 0x114D9B74 or 0x1549A966 or 0x1654AE6B
            or Cluster or 0x1C53BB6B or Attachments or 0x1043A770 or 0x1254C367;
        private static void RequireSize(Element element)
        {
            if (element.End is null) throw Bad("Indeterminate size is allowed only for Segment and Cluster");
            if (element.Id == 0xBF && element.End - element.Data != 4) throw Bad("Invalid CRC-32 size");
        }
        private static InvalidDataException Bad(string message) => new(message + ". Fast Matroska extraction was stopped.");
    }
}
