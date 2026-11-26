# Graphical subtitle OCR service (stage 1)

`SubtitleOcr` is an internal, optional service. It does not change extraction or the GUI.
It accepts an analyzed graphical subtitle stream, renders subtitle bitmaps with the
installed FFmpeg, and passes each visible image to Tesseract 5.x. FFprobe supplies
cue times. The service returns text, time interval, language, error, and diagnostic
information per cue. Rendering uses only the selected subtitle stream, not video frames.

The managed OCR root is the user's local application data `MediaUnseal/ocr` directory.
An optional explicit executable path is checked first, followed by a managed executable,
the usual Windows installation path, and `PATH`. The root has independent
`tessdata_fast` and `tessdata_best` directories. Only `rus`, `eng`, `deu`, `fra`,
`spa`, `ita`, `por`, `chi_sim`, `jpn`, and `kor` are accepted. This stage neither
downloads Tesseract nor installs language data.

Bitmap subtitle rendering uses FFmpeg's subtitle-to-video compatibility path.
This FFmpeg feature is documented as experimental. Empty clear frames are discarded;
visible alpha bounds are cropped before OCR. A DVD VOB without unambiguous IFO palette
metadata is rejected. Output and cue counts are bounded. Temporary images use the
existing owned temporary directory mechanism and are removed on completion, error,
or cancellation.

The integration profile has run with Tesseract 5.4 and official English and Russian
Fast/Best models. It verifies real recognition on PGS and VobSub Matroska samples,
their extracted SUP and IDX/SUB counterparts, cue order and time intervals,
multiline text, invalid and empty images, cancellation, and temporary cleanup.
FFmpeg's subtitle-to-video path can emit several frames with one PTS. Images are
numbered independently and matched to subtitle PTS from FFprobe so that a clear
frame cannot overwrite a visible cue. VobSub display end times are used when
available, including for the final cue. A visible cue with no reliable image or
end time fails instead of being silently omitted. OCR quality remains dependent on source
language and bitmap quality. Linux runtime behavior remains to be tested.

## Stage 2 export route

`SubtitleOcrExport.CreatePlan` creates an internal `TrackExportPlan` for a graphical
subtitle stream. The plan carries an optional explicit language, Fast/Best model,
Tesseract path, model root, and CSV settings. If no language is supplied, the exact
supported language code from the track is used; missing or unsupported codes are
rejected. `UnavailableReasonAsync` checks engine and model availability before an
operation. The existing `TrackExporter` owns output naming, staging, progress,
cancellation, cleanup, and publication. The original SUP or IDX/SUB path is unchanged.

For text output, OCR cues are passed to the existing `SubtitleCsvImporter` writer.
Supported outputs are SRT, WebVTT, ASS, SSA, and universal or ADR CSV. Each visible
cue must produce nonempty text; any failed or empty cue aborts publication and is
reported in diagnostics. Millisecond timing is retained in SRT, WebVTT, and CSV;
ASS/SSA use their existing centisecond representation. Graphical styling and
positioning cannot be reconstructed from OCR. Stage 2 provided the internal route;
the GUI connection is described below.

## Stage 3 GUI route

The existing subtitle format selector retains Original and conditionally enables
the existing SRT, WebVTT, ASS, SSA, and CSV entries for PGS and VobSub streams.
Tesseract and model availability are checked asynchronously when graphical tracks
are selected. The existing subtitle settings window contains OCR language and
Fast/Best controls; its CSV controls are reused. Known track languages are suggested,
including equivalent ISO language codes. Ambiguous or unsupported metadata requires
an explicit selection. Mixed text and graphical selections use their respective
existing conversion and OCR plans. Muxing continues to use its own compatibility
rules. User-facing availability, completion, and error messages have ten interface
translations; technical failure details remain in the diagnostic report.

## PGS timing preflight

Before rendering images or running Tesseract, the OCR route validates every
FFprobe subtitle frame. A visible PGS composition without an explicit duration
may end at the next decoded empty composition (`num_rects=0`): that event clears
the displayed subtitle. The FFprobe value `4294967295` for `end_display_time`
means that the end is unknown. When no explicit duration or clear event supplies
an end, the exporter calculates a bounded interval: at most three seconds and
never past the next later event. The final visible cue receives a three-second
calculated interval. These intervals are not described as source timestamps;
the Diagnostic Report counts and identifies them, and the event journal receives
one warning. Nonmonotonic event order, zero-length clears, and contradictory
explicit intervals still fail before recognition. DVD menu VOB files can contain
navigation loops with nonmonotonic PTS and may need navigation to form a linear
subtitle timeline.
