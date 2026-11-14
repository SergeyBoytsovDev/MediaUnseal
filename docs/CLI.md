# Media Unseal CLI

`mucli.exe` on Windows and `mucli` on Linux run independently of the GUI. Use an absolute path to the executable; no PATH changes are required. The CLI currently handles Matroska attachments only and does not run FFmpeg or FFprobe.

```text
mucli mkv scan -i movie.mkv
mucli mkv scan -i movie.mkv --thorough
mucli mkv info -i movie.mkv --json
mucli mkv structure -i movie.mkv --json
mucli mkv extract -i movie.mkv --all -o output
mucli mkv extract -i movie.mkv --id 123456 -o output
```

`--id` uses the decimal FileUID reported by `scan` or `info`. The output directory must already exist. Extraction never overwrites an existing file and numbers colliding names. Ctrl+C cancels the operation. `--json` provides structured output; `--quiet` prints only a count or byte count for simple timing runs.

Add `--diagnostics` to `scan`, `info`, `structure`, or `extract` to write process timings and `NavigationStats` to stderr. It separates MKV open, EBML search, header/read/position work, and extract preparation, safety checks, file creation, read/write, and flush. Header time includes its read time; do not add those figures together. `searchElapsed`, `matchElapsed`, and `copyElapsed` are reported as `n/a` because those fields belong to the GUI's `TryExtractAsync` path. Standard output, including JSON, remains unchanged. The Main-to-operation timing begins at entry to the CLI process; total time excludes operating-system process creation and shutdown.

For temporary diagnostics, `mucli mkv scan -i movie.mkv --diagnostics-repeat 3` runs Standard discovery three times in one process, reopening the file and creating a new parser for each pass. Add `--thorough` for Thorough discovery. N must be 2–20. Pass timings and `NavigationStats` go to stderr; stdout contains only the first scan result. This option is unavailable for other commands.

`scan`, `info`, and `structure` use Standard discovery: they read the EBML index and stop before media clusters. The result includes a status. `NotFoundByIndex` and `Incomplete` mean that attachments may still exist later in the file. Use `mkv scan --thorough` to search the complete Segment when a definitive result is needed. In quiet mode, this limitation is reported on stderr. Extraction retains its existing search path.

`structure` reports the Segment, SeekHead and Attachments offsets relevant to this attachment reader. It is not a full Matroska inspection tool.

`app version`, `app tools check|install|path`, `app update check|download`, `app config show|set`, `app logs export` and `app languages list` are reserved placeholders. They return `Not implemented` and a nonzero exit code.
